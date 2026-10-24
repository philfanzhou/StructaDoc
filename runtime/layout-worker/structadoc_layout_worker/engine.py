"""Inference engines: the layout-only SDK path behind a narrow interface.

An engine loads once, is reused for every request, and answers one call per
request: decode the image, run layout detection, return the source pixel
dimensions plus regions already normalized to [0, 1]. Engines are synchronous
(the llama.cpp evaluation is CPU/GPU-bound native code); the application runs
them on worker threads and bounds how many run at once.

The shipped engine is the in-process llama.cpp backend of the official MinerU
SDK (``mineru_vl_utils`` with ``mineru_llama_cpp``). The ``mineru`` pipeline
package itself — the torch stack whose entry points are full document parsing —
is deliberately not a dependency of this worker: the SDK client package plus
the llama.cpp engine are the entire layout path, and not installing the
full-parsing stack is itself a guarantee.
"""

from __future__ import annotations

import io
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

from PIL import Image, UnidentifiedImageError

from .config import WorkerConfig
from .sdk_guard import install_full_parsing_guard

ENGINE_IDENTIFIER = "mineru-llama-cpp"
STUB_IDENTIFIER = "stub"


class EngineUnavailableError(RuntimeError):
    """The engine could not answer: the model is unavailable right now."""


class InvalidImageError(ValueError):
    """The request bytes are not a decodable image of the declared format."""


@dataclass(frozen=True)
class Detection:
    """One layout detection result, in the shape the app normalizes from."""

    width: int
    height: int
    # (label, x0, y0, x1, y1) per region, coordinates normalized to [0, 1].
    regions: list[tuple[str, float, float, float, float]]
    model: str | None = None
    version: str | None = None


def decode_image(image_bytes: bytes, expected_media_type: str) -> Image.Image:
    """Decode request bytes as JPEG or PNG and confirm the declared format.

    The decoded image is in its stored orientation: EXIF orientation is not
    applied, because the wire contract's width/height are "the dimensions the
    runtime decoded", in the stored orientation, and the caller is told the
    orientation value separately.
    """
    try:
        image = Image.open(io.BytesIO(image_bytes))
        image.load()
    except (UnidentifiedImageError, OSError, ValueError) as error:
        raise InvalidImageError("the image bytes could not be decoded.") from error

    actual_format = (image.format or "").upper()
    declared_format = "JPEG" if expected_media_type == "image/jpeg" else "PNG"
    if actual_format != declared_format:
        raise InvalidImageError(
            f"the image bytes are {actual_format or 'undetectable'}, "
            f"not the declared {declared_format}.")
    return image


class LayoutEngine(Protocol):
    """The narrow interface the application depends on."""

    identifier: str

    def detect(self, image_bytes: bytes, media_type: str) -> Detection:
        """Run one layout detection. Synchronous and blocking."""
        ...

    def close(self) -> None:
        ...


class StubEngine:
    """A deterministic, weight-free engine used by tests and manual checks.

    It decodes the image for real (so dimensions and format validation behave
    exactly as the real path) and answers with fixed regions. It makes no
    detection claims; it exists so the HTTP surface is testable without the
    multi-gigabyte model files.
    """

    identifier = STUB_IDENTIFIER

    def __init__(self, delay_seconds: float = 0.0):
        self._delay = delay_seconds

    def detect(self, image_bytes: bytes, media_type: str) -> Detection:
        import time

        image = decode_image(image_bytes, media_type)
        if self._delay:
            time.sleep(self._delay)
        return Detection(
            width=image.width,
            height=image.height,
            regions=[
                ("text", 0.1, 0.05, 0.9, 0.2),
                ("figure", 0.25, 0.3, 0.75, 0.7),
                ("other", 0.4, 0.8, 0.6, 0.95),
            ],
            model="stub-layout-model",
            version="0",
        )

    def close(self) -> None:
        return None


class MinerULlamaCppEngine:
    """The official MinerU SDK layout path on the in-process llama.cpp engine.

    The native ``mineru_llama_cpp.Engine`` (GGUF model + mmproj, loaded once
    at startup) is handed to a ``mineru_vl_utils.MinerUClient`` configured for
    the ``llama-cpp-engine`` backend, and every request runs
    ``client.layout_detect`` — the SDK's layout-only entry point. The
    full-parsing guard is installed on the client before the first request.
    """

    identifier = ENGINE_IDENTIFIER

    def __init__(self, config: WorkerConfig):
        if config.model_path is None or config.mmproj_path is None:
            raise EngineUnavailableError(
                "the llama-cpp engine needs model and mmproj paths.")
        try:
            from mineru_llama_cpp import Engine
            from mineru_vl_utils import MinerUClient, __version__ as sdk_version
        except ImportError as error:
            raise EngineUnavailableError(
                "the engine dependencies are not installed; install the "
                "'engine' dependency group (mineru-vl-utils, mineru-llama-cpp)."
            ) from error

        self._sdk_version = str(sdk_version)
        self._model_name = (
            config.model_name
            or Path(config.model_path).stem
            or "mineru-layout-model")
        self._engine = self._create_native_engine(config, Engine)
        self._client = self._create_client(config, self._engine, MinerUClient)
        guarded = install_full_parsing_guard(self._client)
        if not guarded:
            raise EngineUnavailableError(
                "the full-parsing guard found no SDK entry points to block; "
                "the SDK surface has changed and this worker refuses to run.")

    @staticmethod
    def _create_native_engine(config: WorkerConfig, engine_type: Any) -> Any:
        try:
            return engine_type(config.model_path, config.mmproj_path)
        except Exception as error:  # native loader failures take many shapes
            raise EngineUnavailableError(
                "the llama.cpp model could not be loaded; check the model and "
                "mmproj paths and the files' integrity."
            ) from error

    @staticmethod
    def _create_client(config: WorkerConfig, native_engine: Any, client_type: Any) -> Any:
        return client_type(
            backend="llama-cpp-engine",
            llama_cpp_engine=native_engine,
            max_concurrency=config.max_concurrency,
            use_tqdm=False,
            debug=False,
        )

    def detect(self, image_bytes: bytes, media_type: str) -> Detection:
        image = decode_image(image_bytes, media_type)
        try:
            result = self._client.layout_detect(image)
        except Exception as error:
            raise EngineUnavailableError("layout inference failed.") from error

        regions: list[tuple[str, float, float, float, float]] = []
        try:
            for block in result:
                bbox = list(block["bbox"])
                if len(bbox) != 4:
                    raise KeyError("bbox")
                regions.append((
                    str(block["type"]),
                    float(bbox[0]),
                    float(bbox[1]),
                    float(bbox[2]),
                    float(bbox[3]),
                ))
        except (KeyError, TypeError, ValueError) as error:
            raise EngineUnavailableError(
                "the SDK layout output did not carry a type and four "
                "coordinates per region.") from error
        return Detection(
            width=image.width,
            height=image.height,
            regions=regions,
            model=self._model_name,
            version=self._sdk_version,
        )

    def close(self) -> None:
        closer = getattr(self._engine, "close", None)
        if closer is not None:
            try:
                closer()
            except Exception:
                pass


def build_engine(config: WorkerConfig) -> LayoutEngine:
    """Construct the configured engine, loading the model once."""
    from .config import LLAMA_CPP_ENGINE, STUB_ENGINE

    if config.engine_name == STUB_ENGINE:
        return StubEngine()
    if config.engine_name == LLAMA_CPP_ENGINE:
        return MinerULlamaCppEngine(config)
    raise EngineUnavailableError(f"unknown engine '{config.engine_name}'.")
