# Canonical Result Persistence

This note describes Parse Bundle validation and idempotent success commits. Field semantics come from the [Canonical Document Model](../specifications/canonical-document-model.md); state contention follows the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md).

## Persistent Structure

- `parse_pages` uses Parse Run ID plus positive page number as its composite key.
- `parse_blocks` stores globally contiguous sequence, optional page, type, content, normalized bounding box, confidence, and Asset reference.
- `parse_assets` stores internal object references, byte size, and SHA-256 for extracted binary resources.
- `parse_artifacts` stores metadata for Markdown, Provider archives, content lists, layouts, normalized PDFs, and other outputs.
- `parse_runs.result_schema_version`, `result_sha256`, and `provider_metadata_json` store the committed bundle version, idempotency fingerprint, and sanitized Provider metadata.

`storageRef` is internal. Artifacts use `(parseRunId, type, name)` so multiple items of one type remain distinguishable. Asset display names may repeat because UUID identifies the resource. Composite foreign keys ensure Pages and Assets belong to the same Parse Run as their Blocks.

## Bundle Validation

Schema `1.0` validation includes:

- positive unique Page numbers and Block sequences that start at zero and remain contiguous;
- Page and Asset references within the same bundle;
- finite 0–1 bounding boxes with ordered edges and 0–1 confidence;
- lowercase tokens for types, subtypes, content formats, and Artifact types;
- valid UUIDs, media types, positive sizes, lowercase SHA-256, and relative POSIX storage references;
- bounded JSON objects for Provider metadata, source locators, Artifact metadata, and Provider data;
- rejection of credential fields, internal paths, and HTTP(S) URLs containing query strings in extensions.

Current aggregate safeguards allow at most 10,000 Pages, 100,000 Blocks, 10,000 Assets, and 10,000 Artifacts; 4 MiB of characters per Block; 64 MiB of Block content; and 64 MiB of extension JSON. These are internal safety bounds and may be tuned without changing public field semantics.

## Success Commit

`IParseBundleCommitStore`:

1. copies collections into an immutable commit snapshot and validates the bundle;
2. streams every unique `storageRef` and verifies actual size and SHA-256;
3. computes the bundle SHA-256 using deterministic streaming serialization;
4. starts a database transaction and confirms that the Parse Run is still `running`, the lease owner and concurrency version match, and the lease has not expired;
5. writes all canonical rows and marks the Parse Run `succeeded` in the same transaction;
6. clears the lease and prior error and writes completion time, schema, fingerprint, and sanitized Provider metadata.

Replaying the same fingerprint on an already successful run returns `AlreadyCommitted`; a different fingerprint conflicts. Unique-key races, partial pre-existing rows, expired leases, and concurrent cancellation cannot leave partial target rows or overwrite the winning state.

## Public Reading

The result API projects stable DTOs rather than serializing persistence entities. Pages, Blocks, Assets, Artifacts, Markdown, and exports are available through resource-authorized endpoints. Binary content is streamed through controlled endpoints; no public response exposes `storageRef`, external task IDs, checkpoints, or raw Provider block JSON.

## Verification and Remaining Risk

SQLite transaction tests and real PostgreSQL, MySQL, and MariaDB container contracts exercise idempotent canonical commits, including converted Artifacts. Additional production MinerU layouts still require fixture-driven normalization support. Raw Artifacts remain authorized downloads and must continue to receive content-disposition, content-type, size, and cleanup controls independent of metadata validation.
