# File Storage

- Status: Implementation note
- Last updated: 2026-08-12

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
| `Storage:Prefix` | `structadoc` | Deployment key prefix |
| `Storage:AccessKey`, `Storage:SecretKey` | Unset | S3 credentials, supplied together |
| `Storage:ForcePathStyle` | Provider-specific | Enables path-style S3 access, commonly for MinIO |

Every `Storage` key is also settable from `/admin` and follows the precedence in [Service Settings](./service-settings.md), so a deployment can move from a container volume to object storage without being recreated. Both halves of the credential are secrets: they are written and never sent back, and only whether they are set is reported. They are written and cleared together, because a configuration with one and not the other is refused.

Explicit S3 access and secret keys must be provided together. Providing them through deployment secrets remains supported and takes precedence; a key pinned that way is reported as managed externally and cannot be written from the browser. When both are omitted, the AWS SDK default credential chain applies.

Readiness probes local staging write/delete or S3 bucket accessibility as appropriate. `POST /api/v1/admin/settings/storage/test` probes a candidate configuration before it is saved by writing and removing one small object under the configured prefix. It writes rather than lists because a bucket that lists but refuses writes accepts every upload attempt and fails each one, and a local path that exists inside a read-only container looks fine until the first document arrives.

Changing storage does not move anything. Objects already written stay where they are, and their `storageRef` values still point into the location they were written to, so a change is a migration to plan rather than a switch to flip.

## Deletion

Deletion first marks a resource `deletion-pending` and snapshots all referenced objects into a persistent Cleanup Job. The cleanup Worker deletes each object idempotently, retries transient errors with backoff, and removes relational rows only after all storage work succeeds. It also recovers stale `running` jobs.

The local implementation also removes the directories a delete emptied, up to but never including the storage root or its staging directory. Object keys are nested per Parse Run, so without this a deployment that deletes results would keep one empty tree per deleted run forever. A directory that still holds anything ends the walk, and the non-recursive delete is what makes a concurrent write into the same directory safe.

## Remaining Work

- periodic orphan-object discovery and reconciliation;
- malware scanning policy and deeper hostile-document validation;
- quotas, retention policies, and operational usage reporting;
- tested backup/restore procedures for additional S3-compatible products.
