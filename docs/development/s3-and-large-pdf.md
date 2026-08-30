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

When a PDF—including one produced from Office—exceeds a Provider's `MaxFileBytes` or `MaxPages`, the executor creates bounded page segments. Each segment has a deterministic ID and persists its source page range, object, SHA-256, submission checkpoint, external task ID, and stage.

Recovery reuses created segments, submitted external jobs, and downloaded Provider archives. Once all segments normalize successfully, the executor:

1. translates local segment pages back to global source pages;
2. rebuilds one contiguous Block sequence;
3. merges Assets, Artifacts, and Markdown deterministically;
4. commits one canonical parent Parse Bundle transaction.

Segment creation and each submission, download, and normalization checkpoint go through the Parse Run lease session. The persistence boundary atomically checks the parent's running status, owner, lease expiry, and concurrency version while writing Segment state, then returns the advanced lease to the session. Heartbeats therefore continue from the latest version, while cancellation finalization, expiry, or another Worker's takeover rejects the stale mutation.

Large-PDF source reads, seekable copies, Segment object writes and saves, archive reads, and final merge I/O all use the Parse Run's linked execution token. Host shutdown, lease loss, or the maximum execution duration stops subsequent local work. PdfSharp opens and creates one chunk synchronously and cannot be interrupted mid-call, so the executor checks cancellation immediately before and after those calls and before starting each following Segment or the final merge.

If a single page exceeds the Provider limit by itself, the run fails with a stable permanent input error instead of looping or silently truncating it.

Segment objects and Provider results participate in the same durable Cleanup Job as other document resources.

Full-text search, OpenSearch, embeddings, RAG, and metadata/LLM extensions remain outside StructaDoc's product boundary.
