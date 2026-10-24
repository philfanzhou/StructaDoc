"""The ASGI application: one inference route, served exactly as contracted.

Wire protocol (the merged ``MinerULayoutDetectionProvider`` contract):

- ``POST /v1/layout/detect`` with optional ``Authorization: Bearer`` (only
  when a credential is configured), ``Content-Type: application/json``, and
  the body ``{"mediaType": "image/jpeg"|"image/png", "image": "<base64>"}``
  and nothing else on either side of the exchange.
- Success is HTTP 200 with exactly ``width``, ``height``, ``regions`` (each
  region exactly ``label``/``x0``/``y0``/``x1``/``y1``) and optional
  ``engine``/``model``/``version``. No diagnostics, no timestamps, no request
  identifiers on this wire: the adapter rejects unknown properties.
- Status semantics: 413/422 input failure, 401 a wrong or missing bearer
  credential, 429 overload (the bounded queue is full), 503 the worker is
  shutting down or the model is unavailable, 500 nothing else should happen.

Retention: request images, results, and raw model responses stay in bounded
request-scoped memory and are never written anywhere; the model cache
directory the deployment points the engine at is the only on-disk state.
"""

from __future__ import annotations

import asyncio
import base64
import binascii
import hashlib
import hmac
import json
import logging
import time
from typing import Any, Awaitable, Callable

from fastapi import FastAPI, Request, Response
from fastapi.responses import JSONResponse

from .config import WorkerConfig
from .engine import (
    EngineUnavailableError,
    InvalidImageError,
    LayoutEngine,
    build_engine,
    decode_image,
)
from .normalize import MAX_RESPONSE_BYTES, OutputContractError, build_response

logger = logging.getLogger("structadoc.layout_worker")

LAYOUT_ROUTE = "/v1/layout/detect"
HEALTH_ROUTE = "/health/ready"

SUPPORTED_MEDIA_TYPES = ("image/jpeg", "image/png")

# The layout contract's image bound is 32 MiB; base64 inflates by 4/3, so a
# legal request body can reach ~45 MiB. Anything beyond that envelope is
# refused before it is read.
MAX_IMAGE_BYTES = 32 * 1024 * 1024
MAX_BODY_BYTES = 48 * 1024 * 1024

REQUEST_PROPERTY_NAMES = frozenset({"mediaType", "image"})

ERROR_SHUTTING_DOWN = "shutting-down"
ERROR_UNAUTHORIZED = "unauthorized"
ERROR_MEDIA_TYPE = "unsupported-media-type"
ERROR_BODY_TOO_LARGE = "request-too-large"
ERROR_IMAGE_TOO_LARGE = "image-too-large"
ERROR_REQUEST_INVALID = "request-invalid"
ERROR_OVERLOADED = "overloaded"
ERROR_ENGINE_UNAVAILABLE = "engine-unavailable"
ERROR_INTERNAL = "internal"


class WorkerState:
    """Shared, in-memory-only state: the engine and the shutdown flag."""

    def __init__(self, engine: LayoutEngine, config: WorkerConfig):
        self.engine = engine
        self.config = config
        self.shutting_down = False
        self._semaphore = asyncio.Semaphore(config.max_concurrency)
        self._queued = 0


def _error(status: int, code: str) -> JSONResponse:
    # Error bodies are this small and generic on purpose: they never echo any
    # part of the request (an oversize or malformed body may be image data).
    return JSONResponse({"error": code}, status_code=status)


def create_app(config: WorkerConfig, engine: LayoutEngine | None = None) -> FastAPI:
    """Build the application; ``engine`` overrides construction for tests."""

    app = FastAPI(
        title="StructaDoc Layout Worker",
        version="0.1.0",
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )
    state = WorkerState(engine=engine or build_engine(config), config=config)
    app.state.worker = state

    @app.middleware("http")
    async def access_log(
        request: Request,
        call_next: Callable[[Request], Awaitable[Response]],
    ) -> Response:
        started = time.monotonic()
        response = await call_next(request)
        duration_ms = int((time.monotonic() - started) * 1000)
        size = response.headers.get("content-length", "-")
        # Method, path, status, duration, size — and nothing else. No client
        # bytes, no body, no query, no identifiers that could correlate to a
        # stored image on the caller's side.
        logger.info(
            "%s %s %d %dms %s",
            request.method,
            request.url.path,
            response.status_code,
            duration_ms,
            size,
        )
        return response

    @app.exception_handler(EngineUnavailableError)
    async def _engine_unavailable_handler(_request: Request, error: EngineUnavailableError):
        logger.warning("engine unavailable: %s", error)
        return _error(503, ERROR_ENGINE_UNAVAILABLE)

    @app.exception_handler(OutputContractError)
    async def _output_contract_handler(_request: Request, error: OutputContractError):
        logger.error("output contract violation: %s", error)
        return _error(500, ERROR_INTERNAL)

    @app.get(HEALTH_ROUTE)
    async def health_ready() -> JSONResponse:
        if state.shutting_down:
            return JSONResponse({"status": ERROR_SHUTTING_DOWN}, status_code=503)
        return JSONResponse({"status": "ok"})

    @app.post(LAYOUT_ROUTE)
    async def layout_detect(request: Request) -> Response:
        if state.shutting_down:
            return _error(503, ERROR_SHUTTING_DOWN)

        credential_error = _check_bearer(request, state.config.bearer_token)
        if credential_error is not None:
            return credential_error

        media_type_error = _check_content_type(request)
        if media_type_error is not None:
            return media_type_error

        declared_length = request.headers.get("content-length")
        if declared_length is not None:
            try:
                length = int(declared_length)
            except ValueError:
                return _error(422, ERROR_REQUEST_INVALID)
            if length > MAX_BODY_BYTES:
                return _error(413, ERROR_BODY_TOO_LARGE)

        body = await request.body()
        if len(body) > MAX_BODY_BYTES:
            return _error(413, ERROR_BODY_TOO_LARGE)

        parsed_error = _parse_request_body(body)
        if isinstance(parsed_error, JSONResponse):
            return parsed_error
        media_type_value, image_bytes = parsed_error

        if len(image_bytes) > MAX_IMAGE_BYTES:
            return _error(413, ERROR_IMAGE_TOO_LARGE)

        try:
            # Decode before entering the queue: a malformed image must fail
            # fast without occupying an inference slot.
            decode_image(image_bytes, media_type_value)
        except InvalidImageError as error:
            logger.info("rejected an undecodable image: %s", error)
            return _error(422, ERROR_REQUEST_INVALID)

        if not await _admit(state):
            return _error(429, ERROR_OVERLOADED)
        try:
            detection = await asyncio.to_thread(
                state.engine.detect, image_bytes, media_type_value)
        finally:
            _release(state)

        response_body = build_response(detection, state.engine.identifier).to_json_dict()
        payload = json.dumps(response_body, separators=(",", ":")).encode("utf-8")
        if len(payload) > MAX_RESPONSE_BYTES:
            logger.error("serialized response exceeded the 1 MiB wire bound")
            return _error(500, ERROR_INTERNAL)
        return Response(
            content=payload,
            status_code=200,
            media_type="application/json",
        )

    return app


def _check_bearer(request: Request, expected_token: str | None) -> JSONResponse | None:
    if expected_token is None:
        return None
    header = request.headers.get("authorization", "")
    supplied = header[7:] if header.lower().startswith("bearer ") else ""
    # Compare fixed-length digests, so the comparison time does not depend on
    # how much of the supplied value happens to match.
    expected_digest = hashlib.sha256(expected_token.encode("utf-8")).digest()
    supplied_digest = hashlib.sha256(supplied.encode("utf-8")).digest()
    if not supplied or not hmac.compare_digest(expected_digest, supplied_digest):
        return _error(401, ERROR_UNAUTHORIZED)
    return None


def _check_content_type(request: Request) -> JSONResponse | None:
    content_type = request.headers.get("content-type", "")
    media_type = content_type.split(";", 1)[0].strip().lower()
    if media_type != "application/json":
        return _error(415, ERROR_MEDIA_TYPE)
    return None


def _parse_request_body(body: bytes) -> tuple[str, bytes] | JSONResponse:
    """Strict request parsing: exact property set, no echoes on failure."""

    def _reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate property '{key}'")
            result[key] = value
        return result

    try:
        payload = json.loads(body, object_pairs_hook=_reject_duplicates)
    except (ValueError, UnicodeDecodeError):
        return _error(422, ERROR_REQUEST_INVALID)

    if not isinstance(payload, dict):
        return _error(422, ERROR_REQUEST_INVALID)
    if set(payload.keys()) != REQUEST_PROPERTY_NAMES:
        return _error(422, ERROR_REQUEST_INVALID)

    media_type_value = payload["mediaType"]
    encoded_image = payload["image"]
    if not isinstance(media_type_value, str) or media_type_value not in SUPPORTED_MEDIA_TYPES:
        return _error(422, ERROR_REQUEST_INVALID)
    if not isinstance(encoded_image, str) or not encoded_image:
        return _error(422, ERROR_REQUEST_INVALID)

    try:
        image_bytes = base64.b64decode(encoded_image, validate=True)
    except (binascii.Error, ValueError):
        return _error(422, ERROR_REQUEST_INVALID)
    if not image_bytes:
        return _error(422, ERROR_REQUEST_INVALID)

    return media_type_value, image_bytes


async def _admit(state: WorkerState) -> bool:
    """Take an inference slot, or fail fast when the bounded queue is full.

    ``max_concurrency`` requests run at once; at most ``max_queue`` further
    requests may wait for a slot. Beyond that the answer is 429 — the worker
    never queues without bound.
    """
    if state._semaphore.locked() and state._queued >= state.config.max_queue:
        return False
    if state._semaphore.locked():
        state._queued += 1
        try:
            await state._semaphore.acquire()
        finally:
            state._queued -= 1
        return True
    await state._semaphore.acquire()
    return True


def _release(state: WorkerState) -> None:
    state._semaphore.release()
