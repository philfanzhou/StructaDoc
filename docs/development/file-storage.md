# File Storage

- Status: Implementation note
- Last updated: 2026-08-07

## Storage Boundary

Application code depends on `IFileStorage`. Current implementations support the local filesystem and S3-compatible object storage.

Internal `storageRef` values are generated from server resource identities and are not a public contract. Sanitized user filenames are display data only and never participate in physical path or object-key composition.

Every implementation preserves conflict-safe, idempotent writes:

1. stream input while enforcing byte limits and computing SHA-256;
2. commit to a server-generated logical reference;
3. if the reference already has the same size and hash, return it idempotently;
4. if different content exists at that reference, return a conflict and never overwrite it;
5. expose streaming reads, existence checks, and idempotent deletion.

The local implementation writes a random staging file and atomically moves it into place. Empty, oversized, cancelled, and failed writes remove staging data. S3 uses conditional operations and hash metadata to provide the same observable behavior.

Fixed logical keys are used for Provider archives and deterministic derived results so crash recovery can reuse identical objects. Random immutable keys are used where an uncommitted object must not be mistaken for an accepted database snapshot.

Cross-media operations are not treated as one fragile transaction. Database acceptance and durable cleanup jobs handle compensation and deletion. Orphan scanning remains a useful maintenance safeguard for crashes before a resource reference was committed.

## Document Detection

The server currently recognizes:

| Format | Detection |
|---|---|
| PDF | `%PDF-` signature |
| DOC / XLS / PPT | OLE Compound File signature plus the original extension to distinguish the family |
| DOCX / XLSX / PPTX | ZIP structure, `[Content_Types].xml`, and the corresponding Open XML main part |

Client MIME types are not authoritative. Arbitrary ZIP files are not accepted as Office documents. Files containing a VBA project are rejected rather than mislabeled as macro-free Open XML. DOCM, XLSM, and PPTM require separate media types, security policy, and tests before support.

## Upload

```text
POST /api/v1/documents
Content-Type: multipart/form-data
file=<exactly one file>
```

Success returns `201` with a public Document summary. Empty files, invalid filenames, and malformed forms return `400`; size violations return `413`; unsupported or unrecognized formats return `415`.

The endpoint requires an administrator, an authorized OIDC user, or an API client with `documents:write`. Cookie-authenticated writes require antiforgery validation. An OIDC upload records the stable owner `(issuer, subject)`; local administrator and API-client uploads retain their distinct creator facts.

## Configuration

| Key | Default | Meaning |
|---|---:|---|
| `Documents:UploadApiEnabled` | `true` | Maps the protected upload endpoint |
| `Documents:MaxUploadBytes` | `104857600` | Maximum original-document size |
| `Storage:Provider` | `Local` | `Local` or `S3` |
| `Storage:RootPath` | `./data/storage` | Persistent local root |
| `Storage:ServiceUrl` | `null` | Optional S3-compatible endpoint |
| `Storage:Region` | Provider default | S3 region |
| `Storage:Bucket` | Required for S3 | Object bucket |
| `Storage:Prefix` | Empty | Deployment key prefix |
| `Storage:ForcePathStyle` | Provider-specific | Enables path-style S3 access, commonly for MinIO |

Explicit S3 access and secret keys must be provided together through deployment secrets. When omitted, the AWS SDK default credential chain applies. Readiness probes local staging write/delete or S3 bucket accessibility as appropriate.

## Deletion

Deletion first marks a resource `deletion-pending` and snapshots all referenced objects into a persistent Cleanup Job. The cleanup Worker deletes each object idempotently, retries transient errors with backoff, and removes relational rows only after all storage work succeeds. It also recovers stale `running` jobs.

## Remaining Work

- periodic orphan-object discovery and reconciliation;
- malware scanning policy and deeper hostile-document validation;
- quotas, retention policies, and operational usage reporting;
- tested backup/restore procedures for additional S3-compatible products.
