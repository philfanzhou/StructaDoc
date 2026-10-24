# Provider Execution Boundary

This note describes the current execution contract. Provider responsibilities follow [ADR-0002](../adr/0002-parser-provider-abstraction.md); states and recovery follow the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md).

## Internal Contract

`IParseProvider` isolates MinerU protocols and can:

- report native media types, file/page limits, and cancellation capability;
- prepare and submit a task using Parse Run ID, immutable configuration, sanitized options, and a reopenable source stream;
- return a persistent submission checkpoint when remote allocation precedes upload;
- poll by a separate external task ID;
- open final results as streams rather than buffering ZIP or large JSON;
- attempt cancellation when supported.

Provider states remain internal. `ProviderException` exposes a stable code, sanitized message, and transient/configuration/input/permanent/security category. Upstream bodies, tokens, and signed URL queries never enter exceptions or logs.

`ProviderCredential.ToString()` is always `[redacted]`; only adapter request construction can read the value explicitly. This reduces accidental logging but does not replace log review or memory hygiene.

## Layout Detection Execution

`ILayoutDetectionProvider` is a second, separate internal execution contract for the non-archiving
image layout-detection mode of [ADR-0013](../adr/0013-non-archiving-layout-detection.md). It is
synchronous request/response — one JPEG or PNG page image in, normalized layout regions out — and
deliberately not an `IParseProvider` operation, because it takes no Parse Run, no immutable
configuration version, and no persisted result. `IParseProvider`, `ProviderCapabilities`, and the
MinerU Cloud/Local full-parsing adapters are unchanged, and neither side implements the other's
contract, so no path exists from a layout request into full document parsing.

- The request carries validated image bytes plus the media type (`image/jpeg` or `image/png`),
  bounded to 32 MiB; the endpoint that calls the contract owns request validation, authorization,
  and the caller's deadline, which arrive through the cancellation token. Cancellation propagates
  as cancellation, not as a Provider error.
- The result carries normalized regions — a generic lowercase type label plus a box in the
  canonical bounding-box convention of the Canonical Model (Section 9: top-left origin, `x`
  rightward, `y` downward, values in `[0, 1]`) — the runtime-reported source image dimensions,
  engine/model/version information when the runtime supplies it, and a bounded diagnostics slot
  that is empty unless the runtime had a recoverable note.
- The capability is declared by resolution, not by extending `ProviderCapabilities`:
  `ILayoutDetectionProviderResolver.Resolve` reads the `LayoutRuntime` settings section and either
  returns the registered implementation or throws `ProviderException`
  `layout-capability-unsupported` (Configuration category) — the explicit unsupported-capability
  error. A blank `LayoutRuntime:BaseUrl` is the shipped state and resolves the same way.
- The path is non-persistent: the contract, the resolution, and the adapter reference no Document,
  Parse Run, Page, Block, Asset, Artifact, or request-result store and no storage client. An
  architecture test holds that structural guarantee; the end-to-end proof (real stores stay
  empty) belongs to the endpoint task.
- `MinerULayoutDetectionProvider` is the one implementation; its single-route wire protocol is
  described in [MinerU HTTP Provider Adapters](./mineru-http-providers.md). The inference runtime
  itself is a separate deployment component.

### The Public Layout Endpoint

`POST /api/v1/layout/detect` is the surface all of the above serves. It is API-client-only —
guarded by the `layout:detect` scope, with no interactive-session branch — and takes one JPEG or
PNG in the `file` multipart field. The endpoint, not the contract, owns what reaches the runtime:

- Request shape first, body later: a non-multipart request, a missing or wrong `file` field, or a
  second file answers `400 layout-request-invalid` before the body is read. The file section is
  then read directly into a bounded in-memory buffer — no form binding, no temporary files on the
  path — and bytes past the 32 MiB image bound answer `413 layout-input-too-large` mid-copy.
- Validation is against the actual bytes: the signature decides the format (`415
  layout-media-type-unsupported` for anything else or for a declared type that contradicts it),
  and the header dimensions plus EXIF orientation are bounded by fixed constants — 32,000 pixels
  per dimension and 100,000,000 pixels in total, checked against the orientation-adjusted size —
  with no settings keys involved.
- Overload and the deadline: a fixed in-flight cap rejects excess layout requests with `429` plus a short
  `Retry-After` before execution begins (the adapter's semaphore remains the only queue), and the deadline is the caller's cancellation plus the runtime's own
  `LayoutRuntime:Timeout` — there is no second endpoint timer.
- Every Provider failure maps to a stable outcome: unsupported capability `503
  layout-capability-unsupported`, deadline expiry `504 layout-timeout`, other transient failures
  and malformed or oversized runtime output `502` (`layout-runtime-unavailable` /
  `layout-output-invalid`), and a runtime that refused the exchange itself `502
  layout-runtime-rejected` / `503 layout-runtime-unavailable`. Caller cancellation closes the
  connection with no response written.
- The response is versioned and Provider-neutral: request identifier (the correlation ID),
  runtime-decoded dimensions, an explicit `coordinateFrame` statement, the EXIF orientation the
  endpoint derived, regions with response-local IDs and generic labels, engine information when
  the runtime supplies it, and a bounded `diagnostics` slot. An explicitly empty region list is a
  documented success — the runtime looked and found nothing.

## Execution Snapshot

`IParseRunExecutionContextStore` accepts only a current `ParseRunLease` and verifies:

- the Parse Run remains `claimed` or `running`;
- claimant, concurrency version, and unexpired lease match;
- captured logical Provider configuration and version belong together;
- the Document and source object still exist.

It returns document metadata, internal object reference, sanitized options, stage, external ID, decrypted internal checkpoint, and base URL/model/backend/credential from the captured immutable version. It never switches to an administrator's later version. Sensitive values remain internal and are absent from string representations and public DTOs.

## State, Checkpoints, and Heartbeats

`IParseRunStateStore` permits only the live lease to update a stage. Atomic submissions persist the external ID and enter `waiting-provider` in one compare-and-set operation. Cloud first stores an encrypted continuation while remaining `submitting`, then clears it and advances after upload confirmation. External IDs are write-once.

An expired `running` job with an external ID becomes an adoption candidate. One new Worker preserves stage and attempt and resumes the existing task or checkpoint.

`ParseRunLeaseHeartbeat` serializes renewal with stage, external-ID/checkpoint, failure, and canonical result writes. Every operation receives the latest concurrency token. Before final commit it renews the lease, verifies storage, and performs the database transaction under the same session lock. A renewal failure cancels the session token, but all later writes still recheck the database lease because remote requests cannot always be cancelled.

## Recoverable Executor

`ParseRunExecutionWorker` and `ParseRunExecutor` run this sequence:

1. adopt expired resumable external work before claiming new `queued` work;
2. load the captured configuration and source and validate capabilities, media type, and size;
3. when necessary, create or reuse a constrained LibreOffice PDF snapshot;
4. perform an atomic Local submission or checkpointed Cloud submission;
5. poll with bounded delays and stream a validated result ZIP into storage;
6. rebuild a deterministic Parse Bundle from the saved archive;
7. commit canonical results under the final lease and one database transaction.

If a conversion snapshot exists, recovery reuses its PDF. If an archive exists, recovery no longer depends on Provider retention. Transient poll, download, normalization, and storage failures enter `retry-wait` while preserving recoverable state.

For a protocol without a durable submission checkpoint, an unknown submission outcome is not automatically resent; it fails with `provider-submission-outcome-unknown`. This deliberately favors avoiding duplicate external jobs over speculative resubmission.

An administrator configures an enabled default Provider; a user then uploads a document and explicitly creates a Parse Run. Uploading alone does not start parsing. The deployment must also run a Worker (`Worker__Enabled` defaults to `true`). There is no additional execution switch in the administration page: the Provider configuration selects where documents may be sent, while the deployment setting controls which Host processes queued runs.

`Worker:Enabled` remains, and is a deployment choice rather than a pause button: it lets a Host serve the API while other Hosts do the parsing. It is not settable from a browser. Pausing parsing is done by disabling the Provider, which stops new runs from being created; runs already queued still carry their own configuration snapshot and will run.

`GET /api/v1/parse-execution` reports `workerEnabled` to anyone who may read Parse Runs, because a Host with no Workers queues runs behind nothing and the person watching cannot otherwise tell that from a queue about to move.

`Worker:MaxConcurrency` sets how many Parse Runs one Host executes at a time and defaults to `1`. Each slot claims under its own Worker ID, so slots never share a lease and one long-running Parse Run does not block the others. Raise it only within Provider rate limits and the independent `LibreOffice:MaxConcurrency` bound. Multiple Hosts can parallelize further through a server database; SQLite remains single-instance.

`Worker:MaxExecutionDuration` bounds a single attempt end to end, including Provider polling, and defaults to one hour. Exceeding it ends that attempt as a retriable `parse-run-execution-timeout` and frees the slot. The deadline may be shorter or longer than `Worker:LeaseDuration`: shorter guarantees the lease is still valid when the timeout is recorded, and longer relies on the heartbeat that already renews ahead of expiry. `00:00:00` disables the bound.

## Result Intake and Normalization

`IProviderResultIntake` stores Provider ZIP results at a fixed logical key and validates compressed size, entries, expanded size, ratio, paths, duplicates, and special files. `IProviderResultNormalizer` deterministically maps recognized MinerU Markdown, content lists, layouts, model output, and images into canonical resources. Stable logical keys and UUIDs make crash replay produce the same bundle fingerprint.

See [Provider Result Intake](./provider-result-intake.md), [Provider Result Normalization](./provider-result-normalization.md), and [MinerU HTTP Provider Adapters](./mineru-http-providers.md).

## Verified Behavior and Remaining Work

Automated coverage includes capabilities, redaction, duplicate Provider registration, encrypted Cloud checkpoint recovery, token isolation, signed-target SSRF controls, Local multipart, state/error mapping, stream ownership, immutable execution contexts, conditional stages and IDs, adoption, heartbeat concurrency, ZIP limits, Cloud/Local layout recognition, deterministic canonical mapping, and executor-to-commit integration. Real PostgreSQL, MySQL, MariaDB, production-image, and Chromium contracts pass in GitHub Actions.

The layout contract is covered against an instrumented stub runtime: success with normalized regions, the single-route guarantee (any non-layout request fails the test), malformed and oversized output rejection, deadline expiry, cancellation with slot release, the concurrency bound, unsupported-capability resolution, registration with and without a runtime, and the structural no-persistence guarantee. The public endpoint adds the end-to-end half: every outcome row is tested against the real host with a stub provider, the stores stay empty after success, failure, timeout, cancellation, and concurrent runs, no temporary files appear on the path, and the `layout:detect` scope admits API clients while refusing every other scope and interactive sessions.

Remaining work is upstream cancellation support, detailed attempt records, broader production fixtures, deployment-specific base-URL trust policy, and the deployable inference runtime.
