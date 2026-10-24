"""Shared fixtures: a tiny real PNG/JPEG, a worker config, and an app client."""

from __future__ import annotations

import base64
import io

import httpx
import pytest
from PIL import Image

from structadoc_layout_worker.app import LAYOUT_ROUTE, create_app
from structadoc_layout_worker.config import WorkerConfig
from structadoc_layout_worker.engine import StubEngine


def make_png_bytes(width: int = 64, height: int = 32, color: tuple = (10, 20, 30)) -> bytes:
    image = Image.new("RGB", (width, height), color)
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def make_jpeg_bytes(width: int = 48, height: int = 48) -> bytes:
    image = Image.new("RGB", (width, height), (40, 50, 60))
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG")
    return buffer.getvalue()


def encode_request(image_bytes: bytes, media_type: str = "image/png") -> dict:
    return {
        "mediaType": media_type,
        "image": base64.b64encode(image_bytes).decode("ascii"),
    }


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


@pytest.fixture
def config() -> WorkerConfig:
    return WorkerConfig(
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


@pytest.fixture
def engine() -> StubEngine:
    return StubEngine()


@pytest.fixture
def client(config: WorkerConfig, engine: StubEngine):
    app = create_app(config, engine=engine)
    transport = httpx.ASGITransport(app=app)
    client = httpx.AsyncClient(transport=transport, base_url="http://worker")
    client.app = app  # type: ignore[attr-defined]
    yield client
    engine.close()
