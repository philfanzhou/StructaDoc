# MinerU HTTP Provider Adapters

This note describes the current MinerU Cloud and MinerU Local HTTP adapters, the MinerU layout runtime protocol the layout detection adapter speaks, and the cloud layout backend that serves the same mode through the official cloud API. See [ADR-0002](../adr/0002-parser-provider-abstraction.md) for the abstraction, [ADR-0013](../adr/0013-non-archiving-layout-detection.md) and [ADR-0014](../adr/0014-cloud-layout-backend.md) for the layout mode, and the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md) for recovery rules. Upstream protocol changes must remain in adapters and contract tests rather than entering the public API.

## MinerU Cloud

`MinerUCloudParseProvider` uses the signed batch-upload protocol for one StructaDoc Parse Run:

1. `POST /api/v4/file-urls/batch` requests a batch ID and signed upload URL.
2. Under the current lease, atomically store the batch ID and encrypted signed-URL continuation while remaining in `submitting`.
3. Query the batch. Only a `waiting-file` state performs a streaming `PUT` of the original without Provider token or Content-Type; later states do not upload again.
4. Clear the continuation after upload confirmation and enter `waiting-provider`.
5. `GET /api/v4/extract-results/batch/{batchId}` polls the single file.
6. On `done`, stream the ZIP from `full_zip_url`.

The Cloud token is required and is attached only to requests sent to the administrator-configured API base URL. Signed uploads and result-CDN requests never receive it. Signed URLs never enter logs, exceptions, or canonical metadata. They are stored only as encrypted internal continuations and cleared after confirmation or terminal failure.

Cloud API base URLs require HTTPS and no query. Signed upload/result URLs require HTTPS on port 443 with no user-info or fragment. A dedicated transfer client disables proxies and redirects, resolves DNS on each connection, rejects any non-public IPv4/IPv6 answer, and connects to the validated address. Provider API and signed-transfer clients do not share authentication headers or connection pools.

After a crash with an unknown upload response, a new Worker reloads the same batch and continuation. It repeats the PUT only while the remote state is still `waiting-file`; `pending`, `converting`, `running`, or `done` advance local state without resubmission. An explicit remote failure is permanent. Expired or rejected signed URLs do not automatically allocate another batch because that could duplicate an unknown submission; an administrator can create a new Parse Run.

The capability snapshot currently limits a single file to 200 MiB and 600 pages and includes PDF, DOC/DOCX, PPT/PPTX, HTML, and documented image types. It does not claim native XLS/XLSX support. No usable Cloud cancellation endpoint is currently exposed.

## MinerU Local

`MinerULocalParseProvider` keeps the MinerU 3.x asynchronous multipart protocol for a blank backend or any backend without the reserved `v4:` prefix:

1. `POST /tasks` streams one multipart file and requests ZIP, Markdown, middle JSON, model output, content list, and images.
2. `GET /tasks/{taskId}` maps `pending`, `processing`, `completed`, and `failed` into internal Provider states.
3. `GET /tasks/{taskId}/result` streams the result ZIP.

Local base URLs may use HTTP for a trusted host or network. An optional bearer credential supports protected reverse proxies. The adapter reports PDF, common images, and the OOXML formats DOCX, PPTX, and XLSX as native inputs. Legacy DOC, PPT, and XLS uploads are intentionally omitted from that capability: the Parse Run executor uses the LibreOffice PDF fallback and submits the resulting PDF instead. Legacy mode does not invent universal size or page limits absent from the protocol. StructaDoc does not propagate cancellation to Local servers and reports `supportsCancellation: false`, including when the upstream version has a cancellation route.

### Opt-in MinerU 4

Create a new immutable configuration version with backend exactly `v4:flash`,
`v4:basic`, `v4:standard`, or `v4:advanced`. The suffix selects that deployment's
`tier`; `model` is unused. The base URL is the service or reverse-proxy root
(`https://host/mineru`, without `/v1`). Malformed reserved `v4:` selectors fail as
configuration errors before opening a source or making a request. There is no
protocol probing, fallback, or automatic resubmission.

1. `POST /v1/tasks?tier=...&ocr_mode=...&output_formats=zip` streams only one multipart `files` part. Options are query parameters, never form fields. HTTP 202 and a validated `task_id` are required.
2. `GET /v1/tasks/{taskId}` maps `queued` and `running`; `completed` succeeds only with exactly one file whose status is `completed`. `failed`, `canceled`, `partial`, and any completed-task file mismatch are permanent failures. Unknown states retain the ordinary polling behavior.
3. `GET /v1/tasks/{taskId}/result?response_format=zip` streams HTTP 200 ZIP output; 202 is transient not-ready, and 409 is permanent task failure.

V4 has the same supported input subset as legacy Local, a 200 MiB file limit, no
universal page cap, and no cancellation or submission-checkpoint capability.
Returned URLs are ignored; every request is built beneath the configured root.
Existing operation cancellation, disabled redirects, bearer isolation, bounded
JSON, streaming transfer, sanitized errors, intake limits, and conservative unknown
submission-outcome handling apply. A saved task resumes in its original snapshot's
protocol, and a saved archive normalizes without Provider traffic. Upstream task
store loss cannot be recovered by blindly submitting again.

The observed 4.0.12/docvortex 0.5.16 ZIP is handled by the shared
[normalizer](./provider-result-normalization.md#mineru-4-structured-content).
Arbitrary future output schemas and identical output across tiers are not promised.
No additional input formats are enabled by the broader upstream format support.

The normalizer recognizes both Local layouts such as `{document}/{method}/{document}.md` plus nested `images/` and Cloud's root `full.md`. It resolves only unique, validated candidates from the archive manifest.

## MinerU Layout Runtime

`MinerULayoutDetectionProvider` executes layout-only inference (ADR-0013) against the
administrator-configured runtime — the `LayoutRuntime` settings section
([Service Settings](./service-settings.md)), not a Parse Run Provider configuration. It calls
exactly one route and nothing else: no task submission, no polling, no content recognition, no
artifact generation. The route and the JSON shapes below are the contract the layout inference
runtime serves; the runtime itself ships as a separate deployment component built on the official
MinerU SDK layout path.

```text
POST {LayoutRuntime:BaseUrl}/v1/layout/detect
Authorization: Bearer <LayoutRuntime:Credential>     # only when a credential is configured
Content-Type: application/json

{"mediaType": "image/png", "image": "<base64 JPEG or PNG bytes>"}
```

Response — HTTP 200 with `application/json`:

```json
{
  "width": 2480,
  "height": 3508,
  "engine": "mineru",
  "model": "MinerU2.0-Layout-Detect",
  "version": "2.0.7",
  "regions": [
    { "label": "title", "x0": 0.125, "y0": 0.24, "x1": 0.875, "y1": 0.42 }
  ]
}
```

- `width` and `height` are the source image dimensions in pixels as the runtime decoded them;
  `regions` is required (an explicitly empty array is a valid answer for a blank image), each
  region exactly `label`/`x0`/`y0`/`x1`/`y1`; `engine`, `model`, and `version` are optional
  strings.
- Region geometry is normalized: top-left origin, `x` rightward, `y` downward, coordinates in
  `[0, 1]`, `x0 <= x1` and `y0 <= y1`. Labels are short generic lowercase identifiers
  (`text`, `title`, `table`, `figure`, `formula`, ...); the response carries no recognized body
  content.
- Parsing is strict: unknown or duplicate properties, wrong types, missing `width`/`height`/
  `regions`, coordinates outside `[0, 1]` or inverted, labels longer than 32 characters or
  containing anything but `a-z0-9_-`, and more than 4096 regions all fail as
  `mineru-layout-response-invalid` (permanent) with a bounded diagnostic naming the offending
  property or region index — never a successful empty result. The response body is limited to
  1 MiB (`mineru-layout-response-too-large`).
- HTTP mapping follows the shared boundary: runtime `400/422` is input failure, `401/403` is
  configuration failure (a protected runtime whose credential is wrong), `408/429/5xx` and network
  failures are transient (`mineru-layout-http-<status>`, `mineru-layout-network-error`).
- The configured `LayoutRuntime:Timeout` is the client's own deadline, so an expired bound
  surfaces as the transient `mineru-layout-timeout`; caller cancellation propagates as
  cancellation. In-flight executions are bounded by `LayoutRuntime:MaxConcurrency` through a
  semaphore held for the whole exchange.
- The base URL follows the Provider address rule — absolute HTTP(S) without user information, a
  query, or a fragment. HTTP is allowed because a runtime on a trusted network is the expected
  deployment, matching MinerU Local; the adapter must not be pointed at the cloud extraction API,
  which offers no layout-only inference — deployments that want the cloud route select the cloud
  layout backend below instead.

## MinerU Cloud Layout Backend

`MinerUCloudLayoutDetectionProvider` serves the non-archiving layout mode (ADR-0014) when
`Layout:Backend` is `mineru-cloud` ([Service Settings](./service-settings.md)). It reuses the
official cloud extraction protocol the full-parsing cloud adapter speaks — the same routes, the
same credential handling, the same signed-transfer restrictions — inside one request, with none of
the persistent orchestration:

1. The per-request configuration snapshot is loaded from the enabled default Provider
   configuration of type `mineru-cloud`: its current immutable version, base URL, model, and
   credential decrypted through the secret protector. An absent, disabled, or non-cloud default,
   or one without its credential, answers the explicit `layout-capability-unsupported`
   configuration error naming the specific reason.
2. `POST /api/v4/file-urls/batch` allocates one batch with a random `data_id` (there is no Parse
   Run to name it after; the identifier lives in request memory only). The submission asks for
   the least work — `is_ocr=false`, `enable_table=false`, `enable_formula=false` — but the
   service runs its full extraction pipeline regardless, and nothing here promises otherwise.
3. A `waiting-file` state performs the signed `PUT` of the image under the signed-transfer rules:
   no Provider credential, no Content-Type, through the dedicated SSRF-guarded client.
4. `GET /api/v4/extract-results/batch/{batchId}` polls at a one-second cadence inside the single
   overall deadline (120 seconds) that covers submission, upload, polling, download, and
   normalization; `failed` is permanent, unknown states are transient, and an expired deadline
   surfaces as the stable `mineru-layout-timeout` (the endpoint's `504 layout-timeout`).
5. The result ZIP downloads into one bounded in-memory buffer (64 MiB; no temporary file exists
   on the path), and its `layout.json` is reduced to the public layout contract.

Geometry reduction follows the format's own frame. `layout.json` is the cloud's middle JSON: one
page whose `page_size` is the orientation-applied frame the cloud worked in, whose main blocks
(`preproc_blocks`, or the `para_blocks` spelling) carry pixel boxes over that size, and whose
nested `lines[].spans[]` entries are the same regions' contents — never reported as peer regions.
Boxes are normalized against `page_size`, then mapped from the orientation-applied frame back to
the original image's stored frame using the inverse of the submitted image's EXIF orientation, so
the response reports the original dimensions and a consumer applying the reported orientation
rotates exactly once. The page's proportions must match the image's orientation-applied
proportions; unsupported, ambiguous, malformed, or incompatible output — no layout entry, more
than one page, a bad `page_size`, boxes outside the page, an unusable label — is rejected as
`mineru-layout-cloud-layout-response-invalid` (permanent) rather than answered as a false empty
success. A page with no main blocks is a valid empty answer. The engine is reported as
`mineru-cloud`, the model as the configured `model_version`, the version as the cloud's
`_version_name` when present, and the bounded diagnostics always state that the cloud executed
its full extraction pipeline.

The cloud API client carries the credential and the signed-transfer client never does; the two
clients are the ones the full-parsing cloud adapter uses. In-flight exchanges are bounded by a
fixed concurrency limit (4) inside the endpoint's in-flight cap, leaving the shared account room
for Parse Runs. Nothing about the exchange is persisted, and the endpoint's no-logging rules
(image bytes, recognized content, raw responses, credentials, signed URLs) cover this route too.
The minimal setup is: configure the existing MinerU Cloud Provider (the seeded `official`
configuration plus its token), set `Layout:Backend` to `mineru-cloud`, and give the consumer an
API key with `layout:detect` — the MinerU Provider token and the StructaDoc consumer API key are
separate credentials.

## Parse Run Options

Only these non-sensitive JSON properties are accepted. Unknown/duplicate properties, invalid types, and Local-only options in Cloud requests fail before any outbound request with `mineru-options-invalid`.

| Property | Type | Meaning |
|---|---|---|
| `ocr` | boolean | Cloud `is_ocr`; selects Local OCR when `parseMethod` is absent |
| `formula` | boolean | Formula recognition, default `true` |
| `table` | boolean | Table recognition, default `true` |
| `language` | string | OCR language, default `ch` |
| `parseMethod` | `auto`, `txt`, or `ocr` | Local parsing method |
| `effort` | `medium` or `high` | Local hybrid effort |
| `imageAnalysis` | boolean | Local image/chart analysis |
| `startPage` | non-negative integer | Zero-based first page |
| `endPage` | non-negative integer | Zero-based last page, not before `startPage` |

Local `backend` and Cloud `model` come from the immutable Provider configuration version. Runtime options cannot override endpoints, credentials, backend, or model.

In v4, `parseMethod` maps to `ocr_mode` and takes precedence over `ocr`; without
it, `ocr: true` selects `ocr`, otherwise `auto`. PDF-only zero-based inclusive
ranges map to one-based inclusive `page_range`: both ends become `start+1-end+1`,
start only becomes `start+1-r1` (last page), end only becomes `1-end+1`, and no
range is omitted. Overflow or a range on a non-PDF is rejected before source read
or HTTP. Explicit `formula`, `table`, `language`, `effort`, or `imageAnalysis`
is rejected as `mineru-options-invalid`, including values equal to legacy defaults;
absent options are not sent. Callers cannot select a tier or protocol in options.

## HTTP and Error Boundary

- The Cloud submission body is serialized before the request is sent, so it carries a `Content-Length` and a bare `application/json` media type. `JsonContent` cannot state a length and HttpClient then sends the body chunked; a gateway that will not read a chunked request body hands its handler an empty one, which returns as a complaint about the first required field rather than about the framing. These payloads are a few hundred bytes and gain nothing from streaming.
- Response JSON is limited to 1 MiB and response bodies never enter logs or exceptions. The one exception is a Cloud rejection, where the code, message, and trace identifier are carried into the recorded error — and only those. Each is read under both spellings the Provider uses, `code`/`msgCode` and `trace_id`/`traceId`, because its gateway and its extraction API answer differently and reading one spelling loses the trace identifier from half of the rejections. MinerU answers a refused submission with HTTP 200 and a non-zero `code`, so an expired token, an unverified account, and an exhausted quota are otherwise indistinguishable from each other and from the service being broken. Each field is stripped of control characters and truncated, because it is someone else's string on its way into a database row and a browser; the rest of the body is not repeated, since a successful response from that same endpoint carries the presigned upload URL.
- The `code` is checked for being a number before it is read as one. `JsonElement.TryGetInt32` throws rather than returning false for a non-number, and MinerU spells some codes as strings, so the unguarded read turned the responses that carry a reason into an exception outside every Provider failure category.
- Provider API `400/422` is input failure, `401/403` is configuration failure, and `408/429/5xx` plus network timeout is transient.
- A signed transfer `401/403` is not misreported as a Provider credential failure.
- Private, loopback, link-local, reserved, multicast, or non-443 signed targets are security failures; temporary DNS/connection errors remain transient.
- External failures expose stable codes and sanitized messages only.
- `ProviderResultContent` owns and disposes the response stream and `HttpResponseMessage`.
- External task IDs are escaped as one URL path segment; source filenames are safe single-segment values.

## Execution and Remaining Risk

The adapters are wired into the recoverable executor. Configuring one of them as the enabled default Provider is what permits document transfer to it; nothing else is switched on afterwards.

Remaining work includes integration coverage with each deployed MinerU version, upstream cancellation propagation, richer attempt history, and deployment-specific trust policy for administrator-configured base URLs. Local URLs intentionally permit trusted private networks; signed Cloud transfers do not.
