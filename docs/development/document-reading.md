# Document Reading

- Status: Implementation note
- Last updated: 2026-08-07

## Authorization

Document reads require one of:

- an administrator session;
- an OIDC user who owns the document or has a matching read grant;
- an API client with `documents:read`.

`documents:write` does not imply read permission. GET requests do not require antiforgery tokens. Every result, Asset, Artifact, Markdown, export, and original-content route repeats resource-level authorization; endpoint authentication alone is not sufficient.

## Endpoints

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/v1/documents?limit=50&cursor=...` | Lists visible documents in descending creation/ID order |
| `GET` | `/api/v1/documents/{id}` | Returns visible document metadata |
| `GET` | `/api/v1/documents/{id}/content` | Downloads the immutable original |
| `GET` | `/api/v1/documents/{id}/parse-runs` | Lists visible parsing history |

List limits range from 1 to 200 and default to 50. `nextCursor` is opaque: callers pass it back unchanged and do not parse, construct, or persist it as a durable bookmark. It is `null` when no later page exists.

Pagination uses `(createdAt, id)` keysets backed by a composite index instead of offsets. New uploads do not duplicate already-read items during traversal. Pagination is not a database snapshot; callers see newer documents when they restart from the first page.

Public summaries expose the StructaDoc ID, sanitized original filename, detected media type and extension, size, SHA-256, ownership/display facts allowed by the DTO, and timestamps. They never expose `storageRef`, internal metadata, or database keys. Resources in `deletion-pending` are unavailable.

## Content Download

Original-content responses provide:

- `Content-Disposition: attachment` with a safely encoded server filename;
- detected `Content-Type` and `X-Content-Type-Options: nosniff`;
- `Content-Security-Policy: sandbox`;
- a strong SHA-256 ETag and `If-None-Match` support returning `304`;
- byte Range support and `206 Partial Content` where the framework/storage implementation permits it;
- `Cache-Control: private, max-age=0, must-revalidate` so every reuse revalidates authorization and content.

An absent or unauthorized Document returns the resource-hiding response defined by the API. If metadata exists but its object is unavailable, the API returns a generic `503` without an internal path and logs only safe resource identifiers.

Both Local and S3 storage stream reads. The S3 implementation does not buffer an entire object before serving a Range request.

## Related Result Reads

The result API exposes stable Page, Block, Asset, and Artifact DTOs plus controlled content, Markdown, and export routes. Blocks use sequence-based pagination. Raw Provider fields and internal object references are excluded. See [Result API and Resource Lifecycle](./result-api-and-resource-lifecycle.md).

## Remaining Work

- richer metadata filters and cursor versioning for new sort orders;
- quota and retention views;
- additional conditional-request and large-object performance coverage against production S3-compatible services.
