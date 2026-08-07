# Provider Result Intake

This note defines the secure and idempotent boundary for Provider ZIP results. State recovery follows the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md); final resource semantics follow the [Canonical Document Model](../specifications/canonical-document-model.md).

## Intake Sequence

`IProviderResultIntake.StoreArchiveAsync` currently accepts ZIP results only:

1. accept `application/zip`, `application/x-zip-compressed`, or `application/octet-stream` with an actual ZIP signature;
2. stream the Provider response to the fixed internal key `parse-runs/{parseRunId}/provider/result.zip`, enforcing compressed bytes and computing SHA-256;
3. reuse identical existing content at that key, but conflict without overwrite if content differs;
4. reopen the stored object, verify the ZIP signature, and stream every file entry;
5. return an in-memory manifest containing only normalized relative path, compressed/expanded size, and directory status.

This layer retains the raw archive but never extracts its entries directly into production storage. The MinerU normalizer selects known entries from the validated manifest and writes Assets and Artifacts under independent logical keys.

Recovery may call `TryLoadArchiveAsync`, which recomputes the stored size and SHA-256 and repeats all ZIP checks. A valid saved object removes dependence on Provider result retention. The Raw Artifact display name is fixed as `provider-result.zip`; an upstream filename never affects bundle identity.

## Security Limits

Validation rejects:

- absolute paths, backslashes, drive letters, empty segments, `.`/`..`, control characters, and excessive UTF-8 path length;
- cross-platform duplicate paths after NFC normalization and case folding;
- Unix links/special files and Windows reparse points;
- configured limits for archive bytes, central-directory bytes, entry count, entry size, total expansion, or per-entry compression ratio;
- a mismatch between central-directory size declarations and bytes actually streamed;
- empty, corrupt, non-ZIP, ZIP64, multi-volume, or runtime-unreadable archives.

Errors use stable sanitized `ProviderResultIntakeException.ErrorCode` and failure category. Messages do not include malicious entry names or upstream bodies. Security/structure failures remove the just-stored fixed object. Cancellation and transient storage I/O preserve an atomically stored object for later revalidation.

## Configuration

| Key | Default | Meaning |
|---|---:|---|
| `ProviderResults:MaxArchiveBytes` | 512 MiB | Maximum compressed ZIP bytes |
| `ProviderResults:MaxEntryCount` | 20,000 | Maximum directory plus file entries |
| `ProviderResults:MaxEntryBytes` | 256 MiB | Actual expanded bytes per file |
| `ProviderResults:MaxExpandedBytes` | 2 GiB | Actual total expanded bytes |
| `ProviderResults:MaxCompressionRatio` | 200 | Maximum expanded/compressed ratio per file |
| `ProviderResults:MaxEntryPathBytes` | 2,048 | Maximum UTF-8 path bytes |
| `ProviderResults:MaxCentralDirectoryBytes` | 64 MiB | Central-directory scan limit before entry creation |
| `ProviderResults:TemporaryPath` | OS temp `structadoc-provider-results` | Bounded fallback for a non-seekable stored stream |

Temporary files use random names and delete-on-close. Copy length is constrained exactly to the verified stored size. Worker concurrency and container temporary-storage quotas must be budgeted together with these per-archive limits.

## Remaining Work

- extend layout recognition with additional production MinerU Cloud and Local fixtures;
- add more archive-fuzzer and high-contention recovery cases;
- monitor limits against real workload distributions without weakening security defaults.
