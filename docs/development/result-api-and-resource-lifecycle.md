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
```

Blocks use `afterSequence` for stable cursor pagination. Their public DTO excludes `ProviderDataJson` and raw source locators. HTML export is rendered from normalized Markdown; ZIP contains Markdown plus authorized Assets; PDF uses the normalized PDF Artifact.

Every route performs resource-level authorization. OIDC users access owned or explicitly shared documents. Administrators use administrative policy. API clients require the corresponding scope. A resource outside the caller's authorization boundary is not distinguishable through storage metadata.

## Durable Deletion

Deletion is not a fragile synchronous transaction across a database and object store:

1. the API transaction marks the target `deletion-pending` and writes a unique persistent Cleanup Job containing a complete object-reference snapshot;
2. reads stop exposing the pending resource;
3. the cleanup Worker idempotently deletes originals, converted PDFs, Provider archives, PDF segments, segment archives, Assets, and Artifacts;
4. only after all object deletions succeed does a database transaction remove relational rows and mark the job `completed`;
5. transient failures enter exponential `retry-wait`, and stale `running` jobs are recovered.

A non-final Parse Run cannot be deleted, and a Document with active Parse Runs cannot be deleted. This prevents cleanup and execution Workers from racing for the same resources.

Persistent Cleanup Jobs make failed deletion observable and retryable; they do not hide object-storage failures behind prematurely removed database rows.
