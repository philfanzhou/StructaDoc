"""The full-parsing guard: instrumentation that fails if a forbidden SDK
entry point is ever invoked, plus the guarantee that the engine touches only
the layout path.
"""

from __future__ import annotations

import pytest

from structadoc_layout_worker.config import WorkerConfig
from structadoc_layout_worker.engine import (
    Detection,
    MinerULlamaCppEngine,
    StubEngine,
    decode_image,
)
from structadoc_layout_worker.sdk_guard import (
    FULL_PARSING_METHODS,
    FullParsingBlockedError,
    install_full_parsing_guard,
)

from conftest import make_png_bytes


class FakeSdkClient:
    """The MinerUClient surface as mineru_vl_utils 2.0.7 publishes it."""

    def __init__(self):
        self.layout_calls = 0
        self.full_parsing_calls = 0

    def layout_detect(self, image):  # noqa: ANN001
        self.layout_calls += 1
        return []

    def batch_layout_detect(self, images):  # noqa: ANN001
        return [[] for _ in images]

    def aio_layout_detect(self, image):  # noqa: ANN001
        return []

    def aio_batch_layout_detect(self, images):  # noqa: ANN001
        return [[] for _ in images]

    def two_step_extract(self, *args, **kwargs):  # noqa: ANN002, ANN003
        self.full_parsing_calls += 1
        return []

    def content_extract(self, *args, **kwargs):  # noqa: ANN002, ANN003
        self.full_parsing_calls += 1
        return ""

    def extract_with_layout(self, *args, **kwargs):  # noqa: ANN002, ANN003
        self.full_parsing_calls += 1
        return []


def test_guard_replaces_every_full_parsing_entry_point():
    client = FakeSdkClient()
    guarded = install_full_parsing_guard(client)
    assert set(guarded) >= {"two_step_extract", "content_extract", "extract_with_layout"}

    with pytest.raises(FullParsingBlockedError, match="two_step_extract"):
        client.two_step_extract("document.pdf")
    with pytest.raises(FullParsingBlockedError, match="content_extract"):
        client.content_extract("document.pdf")
    with pytest.raises(FullParsingBlockedError, match="extract_with_layout"):
        client.extract_with_layout("document.pdf")
    assert client.full_parsing_calls == 0


def test_guard_leaves_the_layout_path_callable():
    client = FakeSdkClient()
    install_full_parsing_guard(client)
    assert client.layout_detect(object()) == []
    assert client.batch_layout_detect([object()]) == [[]]
    assert client.aio_layout_detect(object()) == []
    assert client.aio_batch_layout_detect([object()]) == [[]]
    assert client.layout_calls == 1


def test_guard_covers_the_documented_full_parsing_surface():
    # Every non-layout extraction entry point of MinerUClient 2.0.7.
    assert FULL_PARSING_METHODS == frozenset({
        "content_extract", "aio_content_extract",
        "batch_content_extract", "aio_batch_content_extract",
        "two_step_extract", "aio_two_step_extract",
        "batch_two_step_extract", "aio_batch_two_step_extract",
        "concurrent_two_step_extract", "aio_concurrent_two_step_extract",
        "stepping_two_step_extract", "aio_stepping_two_step_extract",
        "extract_with_layout", "aio_extract_with_layout",
        "batch_extract_with_layout", "aio_batch_extract_with_layout",
    })


def test_llama_cpp_engine_builds_a_guarded_client_and_calls_only_layout_detect(monkeypatch):
    """The engine constructs the SDK client itself; the test injects a fake.

    The weight-free environment has no SDK installed, so fake modules stand in
    for ``mineru_vl_utils`` and ``mineru_llama_cpp``; construction, guard
    installation, and the call path are the production ones. Any reach into a
    full-parsing entry point raises here, which fails the test.
    """
    import sys
    import types

    llama_module = types.ModuleType("mineru_llama_cpp")
    llama_module.Engine = type("Engine", (), {})
    vl_module = types.ModuleType("mineru_vl_utils")
    vl_module.MinerUClient = object
    vl_module.__version__ = "2.0.7-test"
    monkeypatch.setitem(sys.modules, "mineru_llama_cpp", llama_module)
    monkeypatch.setitem(sys.modules, "mineru_vl_utils", vl_module)

    client = FakeSdkClient()

    def fake_client_factory(config, native_engine, client_type):  # noqa: ANN001
        return client

    def fake_native_engine(config, engine_type):  # noqa: ANN001
        return object()

    monkeypatch.setattr(MinerULlamaCppEngine, "_create_client", staticmethod(fake_client_factory))
    monkeypatch.setattr(
        MinerULlamaCppEngine, "_create_native_engine", staticmethod(fake_native_engine))

    config = WorkerConfig(
        host="127.0.0.1", port=0, bearer_token=None,
        engine_name="llama-cpp",
        model_path="/models/model.gguf", mmproj_path="/models/mmproj.gguf",
        model_name=None, max_concurrency=1, max_queue=1)
    engine = MinerULlamaCppEngine(config)
    assert engine._sdk_version == "2.0.7-test"

    image_bytes = make_png_bytes()
    detection = engine.detect(image_bytes, "image/png")

    assert isinstance(detection, Detection)
    assert detection.width == 64 and detection.height == 32
    assert detection.model == "model"
    assert client.layout_calls == 1
    with pytest.raises(FullParsingBlockedError):
        client.two_step_extract("anything")


def test_decode_image_reports_stored_orientation_dimensions():
    image = decode_image(make_png_bytes(width=20, height=10), "image/png")
    assert (image.width, image.height) == (20, 10)


def test_stub_engine_reports_real_decoded_dimensions():
    detection = StubEngine().detect(make_png_bytes(width=17, height=9), "image/png")
    assert (detection.width, detection.height) == (17, 9)
    assert all(0.0 <= value <= 1.0 for region in detection.regions for value in region[1:])
