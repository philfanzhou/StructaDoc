# MinerU HTTP Provider Adapters

This note describes the current MinerU Cloud and MinerU Local HTTP adapters. See [ADR-0002](../adr/0002-parser-provider-abstraction.md) for the abstraction and the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md) for recovery rules. Upstream protocol changes must remain in adapters and contract tests rather than entering the public API.

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

`MinerULocalParseProvider` targets the current official asynchronous protocol version 2:

1. `POST /tasks` streams one multipart file and requests ZIP, Markdown, middle JSON, model output, content list, and images.
2. `GET /tasks/{taskId}` maps `pending`, `processing`, `completed`, and `failed` into internal Provider states.
3. `GET /tasks/{taskId}/result` streams the result ZIP.

Local base URLs may use HTTP for a trusted host or network. An optional bearer credential supports protected reverse proxies. The adapter reports PDF, common images, DOC/DOCX, PPT/PPTX, and XLS/XLSX; it does not invent universal size or page limits absent from the protocol. The current protocol has no single-task cancellation route.

The normalizer recognizes both Local layouts such as `{document}/{method}/{document}.md` plus nested `images/` and Cloud's root `full.md`. It resolves only unique, validated candidates from the archive manifest.

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

## HTTP and Error Boundary

- Response JSON is limited to 1 MiB and response bodies never enter logs or exceptions. The one exception is a Cloud rejection, where `code`, `msg`, and `trace_id` are carried into the recorded error — and only those three. MinerU answers a refused submission with HTTP 200 and a non-zero `code`, so an expired token, an unverified account, and an exhausted quota are otherwise indistinguishable from each other and from the service being broken. Each field is stripped of control characters and truncated, because it is someone else's string on its way into a database row and a browser; the rest of the body is not repeated, since a successful response from that same endpoint carries the presigned upload URL.
- The `code` is checked for being a number before it is read as one. `JsonElement.TryGetInt32` throws rather than returning false for a non-number, and MinerU spells some codes as strings, so the unguarded read turned the responses that carry a reason into an exception outside every Provider failure category.
- Provider API `400/422` is input failure, `401/403` is configuration failure, and `408/429/5xx` plus network timeout is transient.
- A signed transfer `401/403` is not misreported as a Provider credential failure.
- Private, loopback, link-local, reserved, multicast, or non-443 signed targets are security failures; temporary DNS/connection errors remain transient.
- External failures expose stable codes and sanitized messages only.
- `ProviderResultContent` owns and disposes the response stream and `HttpResponseMessage`.
- External task IDs are escaped as one URL path segment; source filenames are safe single-segment values.

## Execution and Remaining Risk

The adapters are wired into the recoverable executor. Configuring one of them as the enabled default Provider is what permits document transfer to it; nothing else is switched on afterwards.

Remaining work includes integration coverage with each deployed MinerU version, cancellation when upstream provides a contract, richer attempt history, and deployment-specific trust policy for administrator-configured base URLs. Local URLs intentionally permit trusted private networks; signed Cloud transfers do not.
