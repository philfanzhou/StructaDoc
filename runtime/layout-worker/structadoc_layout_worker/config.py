"""Worker configuration, read once from the environment at startup.

Every knob is deployment-owned (environment variables or container settings);
the worker exposes no administrative surface of its own. The Host side of this
deployment is the separate ``LayoutRuntime`` settings section, which needs no
change from this worker.
"""

from __future__ import annotations

import os
from dataclasses import dataclass

LLAMA_CPP_ENGINE = "llama-cpp"
STUB_ENGINE = "stub"
ENGINE_NAMES = (LLAMA_CPP_ENGINE, STUB_ENGINE)

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8080
DEFAULT_MAX_CONCURRENCY = 1
DEFAULT_MAX_QUEUE = 8


class ConfigurationError(RuntimeError):
    """Raised when the environment does not describe a startable worker."""


@dataclass(frozen=True)
class WorkerConfig:
    host: str
    port: int
    bearer_token: str | None
    engine_name: str
    model_path: str | None
    mmproj_path: str | None
    model_name: str | None
    max_concurrency: int
    max_queue: int

    @classmethod
    def from_env(cls, environ: dict[str, str] | None = None) -> "WorkerConfig":
        env = os.environ if environ is None else environ

        host = env.get("LAYOUT_WORKER_HOST", DEFAULT_HOST).strip() or DEFAULT_HOST
        port = _read_int(env, "LAYOUT_WORKER_PORT", DEFAULT_PORT, 1, 65535)
        engine_name = env.get("LAYOUT_WORKER_ENGINE", LLAMA_CPP_ENGINE).strip().lower()

        if engine_name not in ENGINE_NAMES:
            raise ConfigurationError(
                f"LAYOUT_WORKER_ENGINE must be one of {', '.join(ENGINE_NAMES)}, "
                f"not '{engine_name}'.")

        bearer = env.get("LAYOUT_WORKER_BEARER_TOKEN", "").strip()
        if bearer and (len(bearer) > 512 or any(ord(c) < 32 for c in bearer)):
            raise ConfigurationError(
                "LAYOUT_WORKER_BEARER_TOKEN must be a single-line value of at most "
                "512 characters.")

        model_path = env.get("LAYOUT_WORKER_MODEL_PATH", "").strip() or None
        mmproj_path = env.get("LAYOUT_WORKER_MMPROJ_PATH", "").strip() or None
        model_name = env.get("LAYOUT_WORKER_MODEL_NAME", "").strip() or None

        if engine_name == LLAMA_CPP_ENGINE and (model_path is None or mmproj_path is None):
            raise ConfigurationError(
                "The llama-cpp engine requires LAYOUT_WORKER_MODEL_PATH and "
                "LAYOUT_WORKER_MMPROJ_PATH (local GGUF and mmproj files).")

        max_concurrency = _read_int(
            env, "LAYOUT_WORKER_MAX_CONCURRENCY", DEFAULT_MAX_CONCURRENCY, 1, 64)
        max_queue = _read_int(env, "LAYOUT_WORKER_MAX_QUEUE", DEFAULT_MAX_QUEUE, 0, 4096)

        return cls(
            host=host,
            port=port,
            bearer_token=bearer or None,
            engine_name=engine_name,
            model_path=model_path,
            mmproj_path=mmproj_path,
            model_name=model_name,
            max_concurrency=max_concurrency,
            max_queue=max_queue,
        )


def _read_int(
    env: dict[str, str],
    name: str,
    default: int,
    minimum: int,
    maximum: int,
) -> int:
    raw = env.get(name, "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
    except ValueError as error:
        raise ConfigurationError(f"{name} must be an integer, not '{raw}'.") from error
    if not minimum <= value <= maximum:
        raise ConfigurationError(f"{name} must be between {minimum} and {maximum}.")
    return value
