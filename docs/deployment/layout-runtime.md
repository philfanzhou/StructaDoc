# Layout Runtime Deployment

- Last updated: 2026-10-24

This document describes the optional layout inference runtime of
[ADR-0013](../adr/0013-non-archiving-layout-detection.md): a small Python
service that runs the official MinerU SDK's layout-only inference path
(`layout_detect`) in process and serves exactly the wire protocol the
`MinerULayoutDetectionProvider` adapter speaks (see
[MinerU HTTP Provider Adapters](../development/mineru-http-providers.md#mineru-layout-runtime)).

The runtime is a separate deployment component. It is never part of the main
Host image, which contains no Python by design
([Single Container](./single-container.md)); the two are combined only by
pointing the Host's `LayoutRuntime` settings at the runtime's address.

It is optional end to end: with no `LayoutRuntime:BaseUrl` configured, the
Host answers layout requests with an explicit unsupported-capability error,
and every other feature works without this component.

Since [ADR-0014](../adr/0014-cloud-layout-backend.md) this runtime is one of
two selectable backends for the layout mode. A deployment that already runs
the configured MinerU Cloud Provider can set `Layout:Backend` to
`mineru-cloud` instead and needs none of what this document describes — no
worker, model files, Python, or GPU. That route sends the image to the cloud
service, which runs its full extraction pipeline; the Host still archives
nothing. The runtime below remains the default backend and the choice that
keeps inference local. See
[MinerU HTTP Provider Adapters](../development/mineru-http-providers.md#mineru-cloud-layout-backend)
for the cloud exchange.

## What the Worker Does and Does Not Do

- It serves one route, `POST /v1/layout/detect`, and nothing else: no task
  submission, no polling, no content recognition, no artifact generation.
  A guard installed on the SDK client makes any call into a full-parsing or
  content-recognition entry point fail loudly, and the heavy `mineru` parsing
  package is not installed at all — the worker's dependencies are the SDK
  client (`mineru-vl-utils`) and the in-process llama.cpp engine
  (`mineru-llama-cpp`).
- It retains nothing: request images, results, and raw model responses stay
  in bounded request-scoped memory and are never written to disk or logs,
  matching the non-archiving mode (ADR-0013, point 1). The model files the
  deployment supplies are the only on-disk state the worker reads, and it
  writes nothing beside them.
- Its logs carry method, path, status, duration, and response size per
  request — never image bytes, body content, raw model responses, or
  credentials.
- It reports `engine` (`mineru-llama-cpp`), `model` (the GGUF file name, or
  `LAYOUT_WORKER_MODEL_NAME`), and `version` (the `mineru-vl-utils` version)
  so answers identify the runtime that produced them.

## Model Download and Version Pinning

The engine needs two files published in the HuggingFace repository
`jinzhenj/MinerU2.5-Pro-2605-1.2B-GGUF` (also mirrored on ModelScope under
the same name) — the repository MinerU 4.0's own llama.cpp engine selects:

| File | Role | Size |
|---|---|---|
| `MinerU2.5-Pro-2605-1.2B-Q8_0.gguf` | the VLM weights (1.2B, Q8_0 quantization) | 506 MiB |
| `mmproj-MinerU2.5-Pro-2605-1.2B-Q8_0.gguf` | the multimodal projector for image input | 677 MiB |

These are third-party model weights. Downloading them transfers data to and
from HuggingFace or ModelScope, and the files must be treated as untrusted
input supplied to the inference engine: verify the checksum before first use,
store them where only the runtime's user can read them, and re-verify after
any re-download.

The pinned checksums recorded when this guide was written:

| File | SHA-256 |
|---|---|
| `MinerU2.5-Pro-2605-1.2B-Q8_0.gguf` | `3f3523429c880d675c0330cb3de78240452bc40a961d037e24f91ab8488f03e7` |
| `mmproj-MinerU2.5-Pro-2605-1.2B-Q8_0.gguf` | `b6c08ab50352677f7396af45ee60682f353f55488e26911d8154bfc37514d3f8` |

The repository never commits weights. `runtime/layout-worker/download_models.py`
downloads both files from either source and refuses to keep a file whose
SHA-256 does not match the pinned value:

```bash
cd runtime/layout-worker
python3 download_models.py --source huggingface --target ./models
# or, for networks closer to ModelScope:
python3 download_models.py --source modelscope --target ./models
shasum -a 256 models/*.gguf   # check against the table above
```

Software versions are pinned in `runtime/layout-worker/uv.lock`; the engine
group installs exactly `mineru-vl-utils==2.0.7` and `mineru-llama-cpp==0.1.3`
plus the pinned server stack (FastAPI, uvicorn, Pillow). When the pinned
model or package versions change, update the checksum table and the lock file
in the same change.

## Running the Worker

### Container

Build the image from the repository root (the worker image is independent of
the main image):

```bash
docker build --tag structadoc-layout-worker:local runtime/layout-worker
```

Run it with the model directory mounted read-only:

```bash
docker run --detach --name structadoc-layout-worker \
  --restart unless-stopped \
  --publish 8081:8080 \
  --volume /srv/structadoc-layout/models:/models:ro \
  --env LAYOUT_WORKER_MODEL_PATH=/models/MinerU2.5-Pro-2605-1.2B-Q8_0.gguf \
  --env LAYOUT_WORKER_MMPROJ_PATH=/models/mmproj-MinerU2.5-Pro-2605-1.2B-Q8_0.gguf \
  --env LAYOUT_WORKER_BEARER_TOKEN='use-a-secret-manager-or-a-long-random-value' \
  structadoc-layout-worker:local
```

The container listens on 8080 (mapped to 8081 above so it can sit beside a
Host on the same machine), runs as a non-root user, exposes
`GET /health/ready`, and stops on `SIGTERM`. Model loading happens before the
server accepts connections, so a container that passes its health check has a
loaded model; the generous health-check start period covers a slow first
load.

### Local (uv)

```bash
cd runtime/layout-worker
uv sync --locked --group engine
LAYOUT_WORKER_HOST=127.0.0.1 \
LAYOUT_WORKER_PORT=8081 \
LAYOUT_WORKER_MODEL_PATH="$PWD/models/MinerU2.5-Pro-2605-1.2B-Q8_0.gguf" \
LAYOUT_WORKER_MMPROJ_PATH="$PWD/models/mmproj-MinerU2.5-Pro-2605-1.2B-Q8_0.gguf" \
uv run python -m structadoc_layout_worker
```

A model that fails to load stops the process at startup with a clear error
(exit code 3) rather than serving requests without a model.

### Configuration Variables

| Variable | Default | Meaning |
|---|---|---|
| `LAYOUT_WORKER_HOST` | `127.0.0.1` (`0.0.0.0` in the image) | listen address |
| `LAYOUT_WORKER_PORT` | `8080` | listen port |
| `LAYOUT_WORKER_ENGINE` | `llama-cpp` | `llama-cpp` (the shipped engine) or `stub` (a deterministic weight-free engine for checks) |
| `LAYOUT_WORKER_MODEL_PATH` | required for `llama-cpp` | path to the GGUF weights |
| `LAYOUT_WORKER_MMPROJ_PATH` | required for `llama-cpp` | path to the mmproj projector file |
| `LAYOUT_WORKER_MODEL_NAME` | the GGUF file name | the `model` string reported in responses |
| `LAYOUT_WORKER_BEARER_TOKEN` | unset | when set, requests must present it as a bearer credential; unset means an unauthenticated runtime on a trusted network |
| `LAYOUT_WORKER_MAX_CONCURRENCY` | `1` | how many inferences may run at once (1–64) |
| `LAYOUT_WORKER_MAX_QUEUE` | `8` | how many further requests may wait for a slot before the answer is `429` |

Wire bounds fixed by the contract, not configurable: a request body above
48 MiB answers `413`, a decoded image above 32 MiB answers `413`, and a
response is never allowed past 1 MiB or 4096 regions.

## Pointing StructaDoc at the Runtime

Under `/admin` (or through deployment-pinned configuration), set the
`LayoutRuntime` section — see
[Service Settings](../development/service-settings.md):

| Setting | Value for this runtime |
|---|---|
| `LayoutRuntime:BaseUrl` | the worker's address, e.g. `http://127.0.0.1:8081` or `http://layout-worker.internal:8080` |
| `LayoutRuntime:Credential` | the same value as `LAYOUT_WORKER_BEARER_TOKEN`, or empty when the token is unset |
| `LayoutRuntime:Timeout` | above the worst realistic inference time on the chosen hardware (default `00:00:30`) |
| `LayoutRuntime:MaxConcurrency` | 1–64; the number of concurrent Host-to-runtime exchanges |

`LayoutRuntime:MaxConcurrency` accepts values from 1 to 64, and the Host
enforces that range everywhere: the settings page cannot write outside it,
and a value a deployment pins through environment variables or configuration
files **stops the Host at startup** when it falls outside the range — the
same rule every deployment-pinned setting follows (see
[Service Settings](../development/service-settings.md)). A wrong credential
surfaces as a configuration failure on the first layout request, not at
startup, because the Host does not call the runtime until asked.

All four settings take effect after a restart. HTTP is allowed for the base
URL because a runtime on a trusted network is the expected deployment;
protect it with the bearer token or network isolation as appropriate.

## Resource Sizing

CPU inference with the pinned Q8_0 model needs roughly 2 cores and 4 GiB of
memory per concurrent request on top of the engine's own footprint; measured
on the reference hardware below, the process held about 1.9 GiB resident with
the model loaded and one request completed. The first inference after startup
is slower than steady state. Size `LAYOUT_WORKER_MAX_CONCURRENCY` to the cores
available, keep `LayoutRuntime:MaxConcurrency` on the Host at or below the
worker's value, and keep `LayoutRuntime:Timeout` above the worst realistic
end-to-end time for a full page — on the reference hardware (Apple M4, 10
cores, 16 GiB memory, CPU-only), loading the model took about 4 seconds and a
single 300 DPI A4 page answered in about 7 seconds, but no universal latency
target is promised. GPU acceleration is not required; none of the pinned
packages needs a GPU, and the llama.cpp engine falls back to CPU threads
automatically.

Disk: the two model files (1.2 GiB together) plus the image (~1 GiB). The
worker writes no other files.

Input resolution matters: the model sees a fixed-size rendering of the page,
and a very low-resolution rasterization of an otherwise normal page can
legitimately produce an empty region list — the runtime looked and found
nothing it could stand behind. Feed page images at a resolution a person
could read (150 DPI and up for A4), and treat a suspicious empty answer as a
prompt to check the input, not as a defect.

## Graceful Shutdown

`SIGTERM` stops new requests (they answer `503`) and waits for in-flight
requests to finish. The honest limit: a llama.cpp evaluation that is already
running cannot always be interrupted immediately, so an in-flight request
may run to the end of its evaluation before the process exits. A supervisor
with a kill timeout shorter than the worst evaluation (`docker stop -t`,
systemd `TimeoutStopSec`) will cut one off — set both above
`LayoutRuntime:Timeout`.

## Retention

Consistent with the non-archiving mode (ADR-0013, point 1): the worker
creates no Document, Parse Run, Page, Block, Asset, Artifact, or request
record anywhere, stores no request image or result, and keeps request data
only in bounded request-scoped memory. The Host side of the exchange makes
the same guarantee. There is no replay: a caller that needs an answer again
must send the image again.

## Real-Image Smoke Procedure

CI never downloads weights; this procedure is the manual check that model
loading and layout inference work end to end on real hardware. Record which
steps ran, on what hardware, and how long they took — like the evidence
quoted in the worker's delivery pull request.

1. Download and verify the model files (see above).
2. Start the worker with the real engine and watch for the readiness line:
   `curl --fail http://127.0.0.1:8081/health/ready` answers `{"status":"ok"}`
   only after the model has loaded.
3. Send a real page image at a readable resolution (150 DPI and up for A4;
   a photograph or scan of a document page works; use an image you are
   allowed to process):

   ```bash
   base64 -w0 page.png > page.b64
   curl --fail -sS http://127.0.0.1:8081/v1/layout/detect \
     --header 'Content-Type: application/json' \
     --data "{\"mediaType\": \"image/png\", \"image\": \"$(cat page.b64)\"}" \
     | python3 -m json.tool
   ```

   Check the answer by eye against the page: the region boxes should sit on
   the page's titles, text blocks, figures, and tables, and `width`/`height`
   must equal the image's stored pixel dimensions.
4. Repeat once: the model is loaded once and reused; the second request
   should answer without the startup cost.
5. With a credential configured, confirm a request without it answers `401`.
6. Exercise the full path through the Host: configure `LayoutRuntime` under
   `/admin`, restart, and call `POST /api/v1/layout/detect` (API client with
   the `layout:detect` scope) with the same image; the region geometry must
   match the direct call.

The deployment records its own evidence; the repository carries no sample
images.
