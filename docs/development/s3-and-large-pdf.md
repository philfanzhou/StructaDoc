# S3-Compatible Storage and Large-PDF Segmentation

`Storage:Provider` supports `Local` and `S3`. The S3 implementation works with AWS S3, MinIO, and compatible services. Conditional writes prevent silent overwrite, and SHA-256 metadata verifies idempotent replay.

```json
{
  "Storage": {
    "Provider": "S3",
    "ServiceUrl": "https://minio.example.com",
    "Region": "us-east-1",
    "Bucket": "structadoc",
    "Prefix": "production",
    "ForcePathStyle": true
  }
}
```

Access and secret keys may be omitted to use the AWS SDK default credential chain. Explicit credentials must be supplied together through deployment secrets. Readiness verifies bucket access. Public responses never expose object keys.

## Large PDFs

When a PDF—including one produced from Office—exceeds a Provider's `MaxFileBytes` or `MaxPages`, the executor creates bounded page segments. Each segment has a deterministic ID and persists its source page range, object reference, expected size and SHA-256, submission checkpoint, external task ID, and stage.

Before an object write starts, the active Worker persists a fenced `creating` intent for that deterministic segment key. A matching conditional object write advances the intent to `created` through the same lease fence. If cancellation, storage failure, or lease loss interrupts either step, the intent remains reachable: recovery regenerates the same page range, creates a missing object, reuses matching content, and fails permanently with `parse-segment-object-conflict` rather than overwriting different content.

Recovery also reuses completed segments, submitted external jobs, and downloaded Provider archives. Once all segments normalize successfully, the Segment orchestrator:

1. translates local segment pages back to global source pages;
2. rebuilds one contiguous Block sequence;
3. merges Assets, Artifacts, and Markdown deterministically;
4. returns one canonical parent Parse Bundle for the executor's commit.

The current executor still rejects the parent transition to `persisting` because a segmented run has Segment-level external task IDs rather than a Run-level external task ID. [Issue #88](https://github.com/philfanzhou/StructaDoc/issues/88) tracks that existing completion defect; it does not change the Segment recovery guarantees below.

Segment creation and each submission, download, and normalization checkpoint go through the Parse Run lease session. The persistence boundary atomically checks the parent's running status, owner, lease expiry, and concurrency version while writing Segment state, then returns the advanced lease to the session. Heartbeats therefore continue from the latest version, while cancellation finalization, expiry, or another Worker's takeover rejects the stale mutation.

Large-PDF source reads, seekable copies, Segment object writes and saves, archive reads, and final merge I/O all use the Parse Run's linked execution token. Host shutdown, lease loss, or the maximum execution duration stops subsequent local work. PdfSharp opens and creates one chunk synchronously and cannot be interrupted mid-call, so the executor checks cancellation immediately before and after those calls and before starting each following Segment or the final merge.

If a single page exceeds the Provider limit by itself, the run fails with a stable permanent input error instead of looping or silently truncating it.

Both partial and completed Segment intents participate in the same durable Cleanup Job as other document resources. Idempotent cleanup succeeds when a partial intent's object was never created. Objects written by versions predating durable Segment intents and lacking any relational row cannot be discovered or cleaned by this mechanism.

Full-text search, OpenSearch, embeddings, RAG, and metadata/LLM extensions remain outside StructaDoc's product boundary.
