# Parse Job Lifecycle

- Status: Target specification with implemented core lifecycle
- Version: 1.0-draft
- Last updated: 2026-08-30

## 1. Purpose

This specification defines persistent Parse Run statuses, diagnostic stages, atomic claims, leases, retries, cancellation, and crash recovery.

The implementation covers creation and idempotent replay, immutable Provider snapshots, status reads, atomic claim/renewal, recovery of expired unstarted claims, `claimed → running`, retry/failure transitions, due-retry requeueing, execution snapshots restricted by a live lease, conditional stage and external-ID persistence, encrypted Cloud submission checkpoints, adoption of running external jobs, serialized heartbeat sessions, capability-driven LibreOffice fallback, bounded Provider result intake, deterministic MinerU normalization, idempotent canonical success transactions, large-PDF segment recovery, local cancellation, and durable cleanup jobs.

Real execution follows a configured Provider with no further switch. Upstream cancellation propagation and richer attempt-history records remain incomplete.

## 2. Authority

The Parse Run is the authoritative job record presented to users and API clients. Provider status, an in-process queue, and Worker memory are not authoritative.

After restart, StructaDoc recovers from its database, object storage, saved external task IDs, and encrypted checkpoints. The Worker is a logical executor. It currently runs as a `BackgroundService` in the Host as defined by [ADR-0003](../adr/0003-technology-and-single-image-deployment.md), but process placement does not change claim, lease, idempotency, or recovery requirements.

## 3. Stable Status

| Status | Final | Meaning |
|---|---:|---|
| `queued` | No | Persisted and waiting for a Worker claim |
| `claimed` | No | Leased by a Worker but not yet confirmed as externally running |
| `running` | No | Validating, preparing, submitting, polling, downloading, normalizing, or persisting |
| `retry-wait` | No | A transient failure occurred; waiting until the next eligible time |
| `cancel-requested` | No | Cancellation requested; waiting for best-effort handling |
| `succeeded` | Yes | The complete canonical result is persisted and readable |
| `failed` | Yes | A permanent error occurred or retries were exhausted |
| `cancelled` | Yes | StructaDoc stopped processing and will not publish this run as successful |

Public status values cannot be renamed or change meaning within one API major version.

## 4. Diagnostic Stage

`stage` reports progress and aids diagnosis but never replaces stable status. Registered stages include:

- `validating`
- `preparing-source`
- `converting`
- `segmenting`
- `submitting`
- `waiting-provider`
- `downloading`
- `normalizing`
- `persisting`
- `cleaning-up`

Stages may be added within one API major version. Consumers determine finality from `status` only.

## 5. State Machine

```mermaid
stateDiagram-v2
    [*] --> queued
    queued --> claimed: Worker acquires lease
    claimed --> running: execution starts
    claimed --> queued: lease expires before start
    running --> succeeded: canonical bundle committed
    running --> retry_wait: transient failure
    retry_wait --> queued: retry becomes due
    running --> failed: permanent failure or attempts exhausted

    queued --> cancel_requested: cancellation requested
    claimed --> cancel_requested: cancellation requested
    running --> cancel_requested: cancellation requested
    retry_wait --> cancel_requested: cancellation requested
    cancel_requested --> cancelled: cleanup completed
```

Mermaid identifiers `retry_wait` and `cancel_requested` correspond to API values `retry-wait` and `cancel-requested`.

## 6. Creation

One database transaction stores:

- Document ID and initial `queued` status;
- Provider type, logical configuration ID, and immutable version ID;
- complete sanitized parsing-option snapshot;
- source and planned submitted media types;
- maximum attempts and first eligible time;
- caller, creation time, and optional idempotency key.

After the API returns a Parse Run ID, an immediate process exit cannot lose the job.

Provider versions are immutable. Updates create new versions. A version referenced by a non-final Parse Run remains decryptable and usable; disabling it only prevents new references.

## 7. Idempotency

- Callers may provide `Idempotency-Key`.
- Scope includes subject, target Document, and operation.
- A repeated key returns the original Parse Run and never creates another external task.
- Without a key, callers may create another Parse Run to preserve separate parsing history.
- Worker object writes and result commits use Parse Run identity and deterministic logical keys so crash replay is safe.

## 8. Atomic Claim and Lease

The configured SQLite, PostgreSQL, MySQL, or MariaDB database is the job source. A claim:

1. selects a due `queued` candidate;
2. uses a conditional update, concurrency version, or equivalent dialect operation so only one Worker succeeds;
3. writes `claimedBy`, `leaseExpiresAt`, and new attempt facts;
4. commits before any network or file processing begins.

Workers renew leases periodically. Lease duration is longer than the heartbeat interval and tolerates short database disruption.

Claiming remains behind a persistence boundary rather than ordinary EF Core CRUD. Server dialects may optimize with row locks or `SKIP LOCKED`; SQLite uses short write transactions and compare-and-set predicates. A zero-row update loses the claim and must not execute the candidate.

SQLite supports concurrent Workers in one StructaDoc instance only. PostgreSQL, MySQL, and MariaDB support multiple application instances. All databases pass the same claim, renewal, expiry, retry, and commit contract.

### Expired Leases

- Expired `claimed` with no external task ID returns to `queued`.
- Expired `running` with an external task ID is adopted by one new Worker and resumes polling/download.
- Expired pre-submission `running` without an external ID may return to `queued`.
- An unknown outcome during non-checkpointed `submitting` fails conservatively with `provider-submission-outcome-unknown` rather than duplicating a remote job.
- Interrupted `persisting` repeats idempotent object verification and bundle commit.
- When external-task existence is uncertain, use a durable Provider idempotency/checkpoint contract or surface a diagnosable state; never submit blindly.

## 9. Provider Submission

Before submission, verify:

- the Document exists and is not deletion-pending;
- file size, media type, and Provider capabilities;
- the captured configuration version can still be decrypted;
- any required Office conversion and its Artifact are committed;
- current policy permits external transfer to this Provider.

Persist an external task ID immediately under the live lease. Logs contain only safe Parse Run, Provider, and sanitized task identifiers.

For a two-step protocol, persist the external allocation plus encrypted continuation before uploading. A crash then resumes the same allocation instead of creating another task.

## 10. Polling and Result Retrieval

- Bound polling by configured minimum/maximum intervals and Provider recommendations; never busy-poll without limit.
- Bound the attempt as a whole with `Worker:MaxExecutionDuration`, so an unresponsive Provider cannot poll forever. Exceeding it ends the attempt with retriable `parse-run-execution-timeout` rather than holding an execution slot, and its Document, indefinitely. Setting it to `00:00:00` disables the bound and restores unbounded polling.
- Treat `429`, temporary network failure, and recoverable `5xx` according to retry policy.
- Download promptly when the Provider reports completion; do not depend on long Provider retention.
- Stream ZIP, JSON, images, and Markdown into bounded temporary or object storage.
- Validate content type, archive paths, compressed bytes, entry counts, expanded bytes, and compression ratios.
- Once a valid archive is stored, recovery normalizes from that object without another Provider download.

## 11. Normalization and Commit

Success occurs in this order:

1. retain Raw Artifacts and Assets and compute size/hash;
2. build a canonical Parse Bundle;
3. validate it against the Canonical Document Model;
4. verify every referenced object and write Pages, Blocks, Assets, and Artifact metadata idempotently;
5. in one final database transaction, write the complete result and change the Parse Run to `succeeded` with `completedAt`;
6. publish any future completion notification only after commit.

A run cannot be `succeeded` while canonical data is incomplete. If object storage succeeds and database commit fails, retry reuses the same logical keys; later orphan reconciliation handles objects that never obtained a database reference.

Large PDFs use deterministic segment identities and stored per-segment stages/checkpoints. Before writing each Segment object, the Worker computes its expected size and SHA-256 and durably records a `creating` intent with the deterministic storage reference. A successful conditional write advances that intent to `created`. Recovery re-derives the page ranges, rebuilds missing objects, reuses content matching the intent, and permanently rejects conflicting content without overwriting it. Historical objects created before this intent protocol and lacking a relational row remain outside automatic recovery and cleanup.

The parent succeeds only after all segments normalize and merge into one globally ordered bundle. Before segment Markdown is concatenated, its image links are rewritten against that segment's own Asset map to the same segment-prefixed names used by the merged canonical Assets. This keeps equal file names from different segments bound to their own images in Markdown, HTML, and ZIP exports.

Segment intent creation, object confirmation, and every later Segment checkpoint mutation are durably fenced by the parent Parse Run's active lease. The mutation and validation of the parent's running status, owner, unexpired lease, and concurrency version share one atomic database transaction. A successful mutation advances the concurrency version and returns the updated lease to the serialized heartbeat session; expiry, cancellation finalization, or takeover prevents the old Worker from committing Segment state. Cleanup snapshots include deterministic storage references from both partial and completed intents, and treat a missing object as an idempotent deletion success.

## 12. Retry Policy

### Retriable by Default

- DNS, connection interruption, and temporary timeout;
- Provider `429`;
- recoverable Provider or storage `5xx`;
- recoverable state after a lost lease;
- an attempt that exceeded `Worker:MaxExecutionDuration`;
- transient final database transaction failure.

### Permanent by Default

- unsupported, corrupt, or unrecognizable input;
- configured size/page limits exceeded, including an indivisible oversized PDF page;
- invalid parsing options;
- missing configuration, invalid credential, or insufficient Provider permission;
- unsupported result structure that cannot normalize;
- security failure such as path traversal, private signed-transfer target, or archive expansion violation.

### Attempt Records

The target attempt record contains attempt number, Worker ID, start/completion time, failure category/code, retry decision, next eligible time, and sanitized diagnosis. The core lifecycle currently persists aggregate attempt and retry facts; a richer independently queryable attempt history remains future work.

After maximum attempts, status becomes `failed`. Manual retry normally creates a new Parse Run and preserves the old record.

## 13. Cancellation

Cancellation is best-effort:

1. atomically change a non-final run to `cancel-requested`;
2. stop beginning new local steps;
3. request upstream cancellation when supported;
4. explain that unsupported Providers may continue consuming resources;
5. finish required cleanup and mark `cancelled`.

Final statuses never transition. A success/cancellation race uses conditional updates: whichever transition commits first wins, and neither path overwrites an existing final state.

`POST /api/v1/parse-runs/{parseRunId}/cancel` requests cancellation. It requires the same authorization as creating a Parse Run for the Document, and Cookie callers supply an antiforgery token. A request against a `queued`, `claimed`, `running`, or `retry-wait` run returns `202` with the updated record. The call is idempotent through completion: repeating it, including after the run reaches `cancelled`, also returns `202`. A run that already reached `succeeded` or `failed` returns `409`, and an unknown or inaccessible run returns `404`.

Callers must not depend on observing `cancel-requested`. A run with no live lease has nothing to wait out, so cancellation may already be complete when the response is written; only `status` finality is contractual.

The request deliberately leaves any live lease in place. Lease renewal requires `claimed` or `running`, so the request itself stops renewal, which cancels the executing Worker's operation token and prevents further local steps. Completion to `cancelled` then happens on whichever path applies:

- the owning Worker completes it immediately after stopping, guarded by its claim rather than by its now-stale concurrency version;
- Parse Run maintenance completes any `cancel-requested` run whose lease is absent or lapsed, which covers `queued` and `retry-wait` runs and any Worker that crashed mid-cancellation.

The same linked execution token covers local large-PDF reads, seekable copies, Segment object writes, fenced Segment saves, archive reads, and final merge I/O. Host shutdown, lease loss, and the execution deadline therefore stop that work through one cancellation path. The durable fence, rather than cooperative token observation alone, prevents a stale Worker from saving Segment state after cancellation or takeover. PdfSharp opens and creates individual chunks synchronously, so cancellation cannot interrupt an operation already in progress; checkpoints before and after those calls prevent another chunk, Segment, or final merge from starting.

Completion clears the stage, claim, lease, and encrypted submission continuation, and sets `completedAt`. Maintenance and execution are separate Workers, so cancellation completes on a Host whose execution slots are all busy. Error facts from the last attempt are retained for diagnosis; `status` remains the only authority on finality.

The complete upstream cancellation path remains an implementation gap because the current MinerU protocols do not expose a stable single-task cancellation contract. Until then, a run submitted to an online Provider may continue consuming remote resources after StructaDoc reports `cancelled`, and the user-facing workspace states this explicitly.

## 14. Deletion Interaction

- A Document with non-final Parse Runs cannot enter deletion.
- Parse Run creation and Document deletion use the Document concurrency version as one transaction
  boundary: creation both verifies `active` and advances that version before inserting, while
  deletion advances it when marking the Document pending. A concurrent request must reload and
  observe the winner rather than creating work underneath a Cleanup Job.
- Parse Run creation advances the selected Provider Config concurrency version in the same
  transaction. Provider Config deletion either observes the new Parse Run or loses that concurrency
  check, so no Run can reference a configuration version deleted underneath it.
- A non-final Parse Run cannot be deleted; cancelling it first is what makes it deletable.
- Any final Parse Run can be deleted individually, whatever its final status and even as its Document's last one. Its Document survives with its original file and returns to being unparsed.
- Execution never continues with a revoked storage reference.
- Deletion marks resources pending and snapshots all objects into a persistent Cleanup Job.
- Cleanup retries object deletion and removes relational rows only after objects are gone.
- Audit facts must survive according to the audit retention policy rather than disappearing accidentally with business rows.

## 15. Observability

Task logs include safe values for:

- Parse Run ID;
- Document ID;
- Provider type;
- Worker ID;
- attempt number;
- status and stage;
- correlation ID.

Never log Provider tokens, OIDC tokens, storage credentials, presigned URL queries, document content, or unsanitized Provider responses.

## 16. Evolution Work

- upstream cancellation propagation when a stable Provider contract exists;
- independently queryable attempt history;
- webhook contracts;
- bulk administrator operations;
- broader contention/stress testing and orphan-reconciliation scheduling;
- role-based API-only/Worker-only startup modes for the same image.

These additions must preserve current status, lease, idempotency, and recovery semantics.
