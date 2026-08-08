# Provider Configuration and Parse Run Creation

This note describes the current implementation. State semantics follow the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md); Provider boundaries follow [ADR-0002](../adr/0002-parser-provider-abstraction.md).

## Provider Configuration Persistence

Configuration is split into a logical configuration and immutable versions:

- `provider_configs` stores name, Provider type, enabled/default state, and current version ID;
- `provider_config_versions` stores version, base URL, model, backend, and encrypted credential;
- creation produces version 1 and every update creates a new version;
- Provider type cannot change; create another logical configuration to switch types;
- at most one enabled configuration is the global default;
- referenced old versions remain available to existing non-final Parse Runs.

Supported type tokens are `mineru-cloud` and `mineru-local`. A base URL must be an absolute HTTP(S) URL without user-info or fragment. This syntax check does not replace outbound DNS, address-range, redirect, and SSRF policy.

Credentials are encrypted with a purpose-isolated ASP.NET Core Data Protection protector. HTTP responses expose only `hasCredential`, never plaintext or ciphertext. Omitting `credential` on update retains the preceding encrypted value; `clearCredential: true` removes it; supplying both returns `400`.

Administrator Cookie endpoints require antiforgery validation for writes:

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/v1/admin/provider-configs` | Lists each logical configuration's current version without credentials |
| `POST` | `/api/v1/admin/provider-configs` | Creates a logical configuration and version 1 |
| `PUT` | `/api/v1/admin/provider-configs/{id}` | Creates and selects a new immutable version |

## Parse Run Creation

`POST /api/v1/documents/{documentId}/parse-runs` requires an administrator, an OIDC owner/grantee with parse permission, or an API client with `parses:write`. Cookie writes require antiforgery validation.

The request may name `providerConfigId`; otherwise it uses the enabled default. No usable default returns `503`, while an explicit unknown ID returns `404`.

Creation persists the `queued` state, Document ID, Provider type, logical configuration and immutable version IDs, non-sensitive options JSON, source/planned submitted media types, maximum attempts, caller facts, and timestamps. Maximum attempts defaults to 3 and accepts 1–10. Options must be a JSON object no larger than 16 KiB and reject credential/password/secret/token/API-key/authorization fields at any depth.

`submittedMediaType` initially equals the source. Before outbound work, the executor validates the immutable Provider capabilities and size limits. If an Office source requires PDF fallback, it saves an independent conversion snapshot and `normalized-pdf` Artifact.

Callers may send one visible-ASCII `Idempotency-Key` up to 256 characters. Scope includes subject, Document, and operation. The first request returns `201`; a replay returns the original record with `200` and `Idempotency-Replayed: true`, even if the default Provider changed. Without the header, each request creates a distinct Parse Run.

`GET /api/v1/parse-runs/{id}` requires corresponding resource access or `parses:read`. It returns stable status/stage, configuration snapshot facts, sanitized options, media types, attempt count, sanitized errors, and timestamps. It excludes leases, concurrency tokens, internal callers, credentials, checkpoints, and external task IDs.

## Cancellation

`POST /api/v1/parse-runs/{parseRunId}/cancel` requires the same authorization as creating a Parse Run for the Document, and Cookie callers supply an antiforgery token. It moves a `queued`, `claimed`, `running`, or `retry-wait` run to `cancel-requested` with one conditional update, returns `202` with the updated record, stays idempotent through completion, returns `409` for a run that already reached `succeeded` or `failed`, and returns `404` for an unknown or inaccessible run. A run with no live lease may already be `cancelled` when the response is written, so callers depend on `status` finality rather than on observing `cancel-requested`. Completion to `cancelled` is durable and happens on the owning Worker or through Parse Run maintenance. See [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md) section 13.

## Current Gaps

- administrator Provider connectivity testing;
- richer execution-attempt history and upstream cancellation propagation;
- deployment-specific trust policy for configurable Cloud/Local base URLs;
- broader production MinerU and LibreOffice sample coverage.

The workspace and administration area already expose document, result, Provider, and API-client management; they are not future placeholders.
