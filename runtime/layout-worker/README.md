# StructaDoc Layout Worker

The optional layout inference runtime of [ADR-0013](../../docs/adr/0013-non-archiving-layout-detection.md):
a Python 3.12 ASGI service that runs the official MinerU SDK's layout-only
path (`layout_detect`) in process and serves exactly the wire protocol the
StructaDoc `MinerULayoutDetectionProvider` adapter accepts — one route,
`POST /v1/layout/detect`, and nothing else.

It performs layout detection only: no content recognition, no full document
parsing, no artifact generation, and no persistence of request data. It is a
separate deployment component and is never part of the main Host image.

Deployment, configuration, model download with pinned checksums, resource
sizing, and the real-image smoke procedure live in
[the deployment guide](../../docs/deployment/layout-runtime.md).

## Local development

The application and test dependencies install without any model weights:

```bash
uv sync --locked --group test
uv run pytest
```

The test suite is weight-free: it runs the worker against a deterministic
stub engine. One test additionally exercises the real
`MinerULayoutDetectionProvider` (the .NET adapter) against this worker over
HTTP; it is skipped automatically when the `dotnet` SDK is not on `PATH`.

Running the worker with the real engine additionally needs the `engine`
dependency group (`uv sync --locked --group engine`) and the two GGUF model
files — see the deployment guide for the pinned download.

## Layout

- `structadoc_layout_worker/` — the application (configuration, engines, the
  full-parsing guard, request/response normalization, ASGI app, entry point).
- `tests/` — the weight-free pytest suite.
- `adapter-check/` — the small .NET harness the adapter-acceptance test runs
  against a live worker.
- `download_models.py` — checksum-verifying model download.
- `Dockerfile` — the standalone container image (`python:3.12-slim`).
