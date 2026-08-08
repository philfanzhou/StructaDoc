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

`Worker:ExecutionEnabled` must be explicitly enabled and defaults to `false`.

`Worker:MaxConcurrency` sets how many Parse Runs one Host executes at a time and defaults to `1`. Each slot claims under its own Worker ID, so slots never share a lease and one long-running Parse Run does not block the others. Raise it only within Provider rate limits and the independent `LibreOffice:MaxConcurrency` bound. Multiple Hosts can parallelize further through a server database; SQLite remains single-instance.

`Worker:MaxExecutionDuration` bounds a single attempt end to end, including Provider polling, and defaults to one hour. Exceeding it ends that attempt as a retriable `parse-run-execution-timeout` and frees the slot. The deadline may be shorter or longer than `Worker:LeaseDuration`: shorter guarantees the lease is still valid when the timeout is recorded, and longer relies on the heartbeat that already renews ahead of expiry. `00:00:00` disables the bound.

## Result Intake and Normalization

`IProviderResultIntake` stores Provider ZIP results at a fixed logical key and validates compressed size, entries, expanded size, ratio, paths, duplicates, and special files. `IProviderResultNormalizer` deterministically maps recognized MinerU Markdown, content lists, layouts, model output, and images into canonical resources. Stable logical keys and UUIDs make crash replay produce the same bundle fingerprint.

See [Provider Result Intake](./provider-result-intake.md), [Provider Result Normalization](./provider-result-normalization.md), and [MinerU HTTP Provider Adapters](./mineru-http-providers.md).

## Verified Behavior and Remaining Work

Automated coverage includes capabilities, redaction, duplicate Provider registration, encrypted Cloud checkpoint recovery, token isolation, signed-target SSRF controls, Local multipart, state/error mapping, stream ownership, immutable execution contexts, conditional stages and IDs, adoption, heartbeat concurrency, ZIP limits, Cloud/Local layout recognition, deterministic canonical mapping, and executor-to-commit integration. Real PostgreSQL, MySQL, MariaDB, production-image, and Chromium contracts pass in GitHub Actions.

Remaining work is upstream cancellation support, detailed attempt records, broader production fixtures, and deployment-specific base-URL trust policy.
