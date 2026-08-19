# Result API, Exports, and Resource Lifecycle

The public API returns StructaDoc canonical DTOs only: Parse Runs, Pages, Blocks, Assets, and Artifact metadata. Provider raw JSON, internal `storageRef` values, checkpoints, external task IDs, and credentials never appear in normal responses. Binary content is available only through authorized download routes.

```text
GET    /api/v1/documents/{documentId}/parse-runs
GET    /api/v1/parse-runs/{parseRunId}
GET    /api/v1/parse-runs/{parseRunId}/pages
GET    /api/v1/parse-runs/{parseRunId}/blocks
GET    /api/v1/parse-runs/{parseRunId}/assets
GET    /api/v1/parse-runs/{parseRunId}/assets/{assetId}/content
GET    /api/v1/parse-runs/{parseRunId}/artifacts
GET    /api/v1/parse-runs/{parseRunId}/artifacts/{artifactId}/content
GET    /api/v1/parse-runs/{parseRunId}/markdown
GET    /api/v1/parse-runs/{parseRunId}/exports/{markdown|html|zip|pdf}
POST   /api/v1/parse-runs/{parseRunId}/cancel
DELETE /api/v1/parse-runs/{parseRunId}
DELETE /api/v1/documents/{documentId}
```

Blocks use `afterSequence` for stable cursor pagination. Their public DTO excludes `ProviderDataJson` and raw source locators.

## Exports

Provider Markdown references images by the Provider's own archive layout, typically `images/<name>`, which resolves to nothing once the Markdown leaves that archive. Exports therefore translate image links onto the resources they actually ship:

- **markdown** returns the canonical Markdown Artifact byte for byte. It is the traceable artifact, so it is never rewritten; its links stay Provider-relative.
- **zip** contains `document.md` plus authorized Assets under `assets/`, with the bundled Markdown's image links pointing at those entries. Colliding Asset file names are disambiguated by Asset ID.
- **html** renders the Markdown to a single self-contained page with Assets inlined as `data:` URIs, bounded per Asset and in total; Assets beyond the budget keep their original link.
- **pdf** returns the `normalized-pdf` Artifact when an Office source required conversion, and otherwise the original Document when it is already a PDF. A Parse Run with neither returns `409`.

Link rewriting matches on file name, because the canonical Asset display name is the archive entry's final segment. A file name shared by more than one Asset is ambiguous and is left untouched rather than guessed, and an unmatched link is preserved so an export never silently drops a reference it could not resolve. Absolute and `data:` targets are never rewritten.

Every route performs resource-level authorization. Administrators use administrative policy. Every other caller accesses what it owns or was explicitly shared, and that includes API clients: a scope decides which verbs a key may use, ownership decides which resources, and holding `parses:read` does not make another principal's Parse Run readable. See [ADR-0008](../adr/0008-api-client-resource-isolation.md). A resource outside the caller's authorization boundary is not distinguishable through storage metadata.

## Durable Deletion

Deletion is not a fragile synchronous transaction across a database and object store:

1. the API transaction marks the target `deletion-pending` and writes a unique persistent Cleanup Job containing a complete object-reference snapshot;
2. reads stop exposing the pending resource;
3. the cleanup Worker idempotently deletes originals, converted PDFs, Provider archives, PDF segments, segment archives, Assets, and Artifacts;
4. only after all object deletions succeed does a database transaction remove relational rows and mark the job `completed`;
5. transient failures enter exponential `retry-wait`, and stale `running` jobs are recovered.

A Parse Run is deletable on its own terms once it is final, whether it succeeded, failed, or was cancelled, and whether or not it is the last one its Document has. Deleting a succeeded run removes everything that run produced — Pages, Blocks, Assets, Artifacts, and segments in the database, and the images, canonical Markdown, Provider archive, PDF segments, segment archives, and converted PDF in storage — while leaving the Document and its original file untouched. A Document that loses its last Parse Run is an unparsed Document again and can be parsed afresh. Local storage prunes the directories a deleted run emptied, so nothing survives the run but the tree it shared with others.

A non-final Parse Run cannot be deleted, and a Document with active Parse Runs cannot be deleted. This prevents cleanup and execution Workers from racing for the same resources. Cancellation is therefore the supported way to release a Document whose Parse Run will never complete on its own, including every run created on a Host started without Workers. See [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md) section 13.

Persistent Cleanup Jobs make failed deletion observable and retryable; they do not hide object-storage failures behind prematurely removed database rows.
