# Canonical Result Persistence

This note describes Parse Bundle validation and idempotent success commits. Field semantics come from the [Canonical Document Model](../specifications/canonical-document-model.md); state contention follows the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md).

## Persistent Structure

- `parse_pages` uses Parse Run ID plus positive page number as its composite key; nullable `label` and `label_source` hold the printed page label and where it came from.
- `parse_blocks` stores globally contiguous sequence, optional page, type, content, normalized bounding box, confidence, and Asset reference; nullable `caption` and `footnote` hold the text a figure, table, chart, or code Block carries with it.
- `parse_assets` stores internal object references, byte size, and SHA-256 for extracted binary resources.
- `parse_artifacts` stores metadata for Markdown, Provider archives, content lists, layouts, normalized PDFs, and other outputs.
- `parse_runs.result_schema_version`, `result_sha256`, and `provider_metadata_json` store the committed bundle version, idempotency fingerprint, and sanitized Provider metadata.

`storageRef` is internal. Artifacts use `(parseRunId, type, name)` so multiple items of one type remain distinguishable. Asset display names may repeat because UUID identifies the resource. Composite foreign keys ensure Pages and Assets belong to the same Parse Run as their Blocks.

## Bundle Validation

Schema `1.1` validation includes:

- positive unique Page numbers and Block sequences that start at zero and remain contiguous;
- Page `label` and `labelSource` set together, a label of 1 to 256 printable characters, and a registered `labelSource`;
- Block `caption` and `footnote` each null or non-blank text (`invalid-block-annotation` otherwise);
- Page and Asset references within the same bundle;
- finite 0–1 bounding boxes with ordered edges and 0–1 confidence;
- lowercase tokens for types, subtypes, content formats, and Artifact types;
- valid UUIDs, media types, positive sizes, lowercase SHA-256, and relative POSIX storage references;
- bounded JSON objects for Provider metadata, source locators, Artifact metadata, and Provider data;
- rejection of credential fields, internal paths, and HTTP(S) URLs containing query strings in extensions.

Current aggregate safeguards allow at most 10,000 Pages, 100,000 Blocks, 10,000 Assets, and 10,000 Artifacts; 4 MiB of characters for each Block's content, caption, and footnote; 64 MiB of Block content, captions, and footnotes together; and 64 MiB of extension JSON. These are internal safety bounds and may be tuned without changing public field semantics.

New bundles must carry the current schema version. Runs committed under `1.0` keep that stored version and stay readable, because no read path checks it; their Pages have null labels and their Blocks null captions and footnotes, with no backfill.

## Page Labels

`PageLabelEnricher` (Application) adds printed page labels to every normalized bundle before the conversion Artifact is added and before commit, on both commit paths of `ParseRunExecutor`: after normalization, and after the segmented large-PDF merge. It reads the document the executor already submits to the Provider, so a converted Office file uses its `normalized-pdf` and a segmented PDF is read once in full. The rules, including the fallback to `page-number` Blocks, are in [Canonical Document Model §7.1](../specifications/canonical-document-model.md#71-printed-page-labels).

`PdfSharpPageLabelReader` (Adapters) is the only code that opens the PDF for this purpose. It copies the stream to a delete-on-close temporary file, opens it with PDFsharp, and walks only the `/PageLabels` tree within the depth, node, and cycle bounds. It checks cancellation before and after the synchronous open and between tree nodes. A PDF that cannot be parsed and a malformed tree are results, not exceptions: the executor logs one warning naming the Parse Run and `unreadable-pdf` or `malformed-page-label-tree`, never content or paths, and the run continues. Exceptions from opening or reading the stored document propagate to the executor's existing transient failure, so a retry cannot commit a result computed from a partial read. The step keeps no state and adds no lease or transaction behavior.

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

The result API projects stable DTOs rather than serializing persistence entities. Pages (including the optional `label` and `labelSource`), Blocks (including the optional `caption` and `footnote`), Assets, Artifacts, Markdown, and exports are available through resource-authorized endpoints. Binary content is streamed through controlled endpoints; no public response exposes `storageRef`, external task IDs, checkpoints, or raw Provider block JSON.

## Verification and Remaining Risk

SQLite transaction tests and real PostgreSQL, MySQL, and MariaDB container contracts exercise idempotent canonical commits, including converted Artifacts. Additional production MinerU layouts still require fixture-driven normalization support. Raw Artifacts remain authorized downloads and must continue to receive content-disposition, content-type, size, and cleanup controls independent of metadata validation.
