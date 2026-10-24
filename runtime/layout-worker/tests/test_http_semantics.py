"""HTTP status semantics: 413/422 input, 401 credential, 429 overload, 503."""

from __future__ import annotations

import asyncio
import base64

import httpx
import pytest

from structadoc_layout_worker.app import LAYOUT_ROUTE, create_app
from structadoc_layout_worker.config import WorkerConfig
from structadoc_layout_worker.engine import (
    EngineUnavailableError,
    StubEngine,
)

from conftest import encode_request, make_png_bytes

MAX_BODY_BYTES = 48 * 1024 * 1024
MAX_IMAGE_BYTES = 32 * 1024 * 1024


def _worker_config(**overrides) -> WorkerConfig:
    values = dict(
        host="127.0.0.1",
        port=0,
        bearer_token=None,
        engine_name="stub",
        model_path=None,
        mmproj_path=None,
        model_name=None,
        max_concurrency=1,
        max_queue=2,
    )
    values.update(overrides)
    return WorkerConfig(**values)


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


@pytest.mark.anyio
async def test_missing_bearer_is_refused_when_a_credential_is_configured():
    app = create_app(_worker_config(bearer_token="secret-token"), engine=StubEngine())
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        response = await client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
        assert response.status_code == 401
        assert response.json()["error"] == "unauthorized"


@pytest.mark.anyio
async def test_wrong_bearer_is_refused():
    app = create_app(_worker_config(bearer_token="secret-token"), engine=StubEngine())
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        response = await client.post(
            LAYOUT_ROUTE,
            json=encode_request(make_png_bytes()),
            headers={"authorization": "Bearer wrong-token"},
        )
        assert response.status_code == 401


@pytest.mark.anyio
async def test_correct_bearer_is_accepted():
    app = create_app(_worker_config(bearer_token="secret-token"), engine=StubEngine())
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        response = await client.post(
            LAYOUT_ROUTE,
            json=encode_request(make_png_bytes()),
            headers={"authorization": "bearer secret-token"},
        )
        assert response.status_code == 200


@pytest.mark.anyio
async def test_unconfigured_worker_serves_without_authentication():
    app = create_app(_worker_config(bearer_token=None), engine=StubEngine())
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        response = await client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
        assert response.status_code == 200


@pytest.mark.anyio
async def test_body_above_the_bound_answers_413():
    app = create_app(_worker_config(), engine=StubEngine())
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        padding = "A" * (MAX_BODY_BYTES + 1024)
        body = '{"mediaType": "image/png", "image": "' + padding + '"}'
        response = await client.post(
            LAYOUT_ROUTE,
            content=body.encode("ascii"),
            headers={"content-type": "application/json"},
        )
        assert response.status_code == 413
        assert response.json()["error"] == "request-too-large"


@pytest.mark.anyio
async def test_decoded_image_above_the_bound_answers_413():
    app = create_app(_worker_config(), engine=StubEngine())
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        oversized = base64.b64encode(b"\x89" + b"0" * (MAX_IMAGE_BYTES + 1)).decode("ascii")
        body = '{"mediaType": "image/png", "image": "' + oversized + '"}'
        response = await client.post(
            LAYOUT_ROUTE,
            content=body.encode("ascii"),
            headers={"content-type": "application/json"},
        )
        assert response.status_code == 413
        assert response.json()["error"] == "image-too-large"


@pytest.mark.anyio
async def test_overload_answers_429_beyond_the_bounded_queue():
    engine = StubEngine(delay_seconds=0.25)
    config = _worker_config(max_concurrency=1, max_queue=1)
    app = create_app(config, engine=engine)
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        # One in flight, one queued, the third is beyond the bound.
        responses = await asyncio.gather(*[
            client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
            for _ in range(3)
        ])
        statuses = sorted(response.status_code for response in responses)
        assert statuses == [200, 200, 429]
        assert responses[statuses.index(429)].json()["error"] == "overloaded"


@pytest.mark.anyio
async def test_concurrency_bounded_requests_all_succeed_when_queue_absorbs_them():
    engine = StubEngine(delay_seconds=0.1)
    config = _worker_config(max_concurrency=2, max_queue=4)
    app = create_app(config, engine=engine)
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        responses = await asyncio.gather(*[
            client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
            for _ in range(6)
        ])
        assert [response.status_code for response in responses] == [200] * 6


@pytest.mark.anyio
async def test_shutting_down_worker_answers_503():
    app = create_app(_worker_config(), engine=StubEngine())
    app.state.worker.shutting_down = True
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        response = await client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
        assert response.status_code == 503
        assert response.json()["error"] == "shutting-down"


class FailingEngine(StubEngine):
    def detect(self, image_bytes, media_type):  # noqa: ANN001
        raise EngineUnavailableError("the model is unavailable right now")


@pytest.mark.anyio
async def test_engine_failure_answers_503_model_unavailable():
    app = create_app(_worker_config(), engine=FailingEngine())
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        response = await client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
        assert response.status_code == 503
        assert response.json()["error"] == "engine-unavailable"
