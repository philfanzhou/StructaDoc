"""No-persistence inspection: nothing is written, and logs stay clean.

The worker's only on-disk state is the model cache the deployment points the
engine at; request images, results, and raw model responses are never written
anywhere. These tests serve requests and then check both halves of that
statement.
"""

from __future__ import annotations

import base64
import logging
import os

import httpx
import pytest

from structadoc_layout_worker.app import LAYOUT_ROUTE, create_app
from structadoc_layout_worker.config import WorkerConfig
from structadoc_layout_worker.engine import StubEngine

from conftest import encode_request, make_jpeg_bytes, make_png_bytes


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


def _watched_tree(root, skip_names):  # noqa: ANN001
    snapshot = {}
    for directory, subdirectories, files in os.walk(root):
        subdirectories[:] = [d for d in subdirectories if d not in skip_names]
        for name in files:
            path = os.path.join(directory, name)
            snapshot[path] = os.path.getsize(path)
    return snapshot


@pytest.mark.anyio
async def test_serving_requests_writes_no_files(tmp_path, monkeypatch):
    # The worker runs with CWD inside a sandbox directory, so any write the
    # application could make lands somewhere observable.
    monkeypatch.chdir(tmp_path)
    os.makedirs(tmp_path / "model-cache", exist_ok=True)

    config = WorkerConfig(
        host="127.0.0.1", port=0, bearer_token="log-secret-token",
        engine_name="stub", model_path=None, mmproj_path=None,
        model_name=None, max_concurrency=1, max_queue=2)
    app = create_app(config, engine=StubEngine())

    before = _watched_tree(tmp_path, skip_names={".pytest_cache", "__pycache__"})
    async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
        for image, media_type in (
            (make_png_bytes(width=32, height=16), "image/png"),
            (make_jpeg_bytes(), "image/jpeg"),
        ):
            response = await client.post(
                LAYOUT_ROUTE,
                json=encode_request(image, media_type),
                headers={"authorization": "Bearer log-secret-token"})
            assert response.status_code == 200
        # A rejected request must not leave anything behind either.
        await client.post(LAYOUT_ROUTE, json={"mediaType": "image/png", "image": "not-base64"})
        await client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))

    after = _watched_tree(tmp_path, skip_names={".pytest_cache", "__pycache__"})
    assert before == after


@pytest.mark.anyio
async def test_logs_carry_no_image_bytes_body_content_or_credentials(caplog):
    config = WorkerConfig(
        host="127.0.0.1", port=0, bearer_token="log-secret-token",
        engine_name="stub", model_path=None, mmproj_path=None,
        model_name=None, max_concurrency=1, max_queue=2)
    app = create_app(config, engine=StubEngine())

    png_bytes = make_png_bytes(width=32, height=16)
    encoded = base64.b64encode(png_bytes).decode("ascii")

    with caplog.at_level(logging.INFO, logger="structadoc.layout_worker"):
        async with httpx.AsyncClient(
                transport=httpx.ASGITransport(app=app), base_url="http://worker") as client:
            await client.post(
                LAYOUT_ROUTE,
                json=encode_request(png_bytes),
                headers={"authorization": "Bearer log-secret-token"})
            await client.post(
                LAYOUT_ROUTE,
                json={"mediaType": "image/png", "image": "c2VjcmV0LWJvZHk="},
                headers={"authorization": "Bearer log-secret-token"})

    logged = caplog.text
    assert logged  # the access log did run
    assert "log-secret-token" not in logged
    assert encoded not in logged
    assert "c2VjcmV0LWJvZHk=" not in logged
    assert png_bytes[:16] not in logged.encode("utf-8", errors="ignore")
    for line in logged.splitlines():
        # Every access-log line ends with exactly method, path, status,
        # duration, size — and nothing that names request content.
        if "/v1/layout/detect" in line:
            tokens = line.split(" ")
            assert tokens[-5] in {"POST", "GET"}, line
            assert tokens[-4] == "/v1/layout/detect"
            assert tokens[-3] in {"200", "422"}, line
            assert tokens[-2].endswith("ms"), line


def test_response_never_carries_request_data():
    # The success response is built only from engine output; a direct check
    # that the encoded image never round-trips into the response body.
    from structadoc_layout_worker.engine import Detection
    from structadoc_layout_worker.normalize import build_response

    detection = Detection(width=10, height=10, regions=[("text", 0.0, 0.0, 1.0, 1.0)])
    payload = build_response(detection, "stub").to_json_dict()
    assert set(payload) == {"width", "height", "regions", "engine"}
    assert payload["engine"] == "stub"
