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
| `DELETE` | `/api/v1/admin/provider-configs/{id}` | Removes a logical configuration and every version of it, or refuses |
| `GET` | `/api/v1/admin/provider-types` | Describes each type: official base URL, and what a blank `model`/`backend` sends |

Deletion exists because a configuration created by mistake would otherwise stay in the list forever. It removes rows rather than hiding them, so it only applies while nothing points at one. A Parse Run that has not reached a final status still reads its configuration version as it executes, and a finished one keeps that version as the record of how its result was produced; both refuse with `409` and separate detail text, because one clears on its own and the other never does. Disabling is how a configuration that has been used is retired.

A Parse Run records its configuration by ID rather than through a foreign key, so nothing in the schema would stop a run created while an administrator is deciding. The check and the delete therefore share one transaction.

## The Official Endpoint

A deployment that has no Provider at all configures one on startup: `official`, type `mineru-cloud`, the hosted service's published base URL, model `vlm`, enabled and default. Assembling that first configuration out of an address, a model token, and a default marker is the step a first-run administrator has no way to get right, and getting it wrong is indistinguishable from a service that cannot parse.

It is created without a credential, deliberately. The token belongs to the deployment's own MinerU account, so no image can carry one, and an administrator supplying it is the only remaining step before the deployment parses.

Seeding is skipped as soon as any Provider exists, so it never competes with a configuration someone made. A deployment whose only Provider was deleted is configured again on the next start, which is the same state a fresh volume would be in. It runs after the business database is migrated and cannot stop startup: a database an administrator has to repair from `/admin` would otherwise take the administration area down with it.

`ProviderTypeDescriptors` states which types authenticate every call. `mineru-cloud` does, so a configuration of that type without a stored credential is refused at Parse Run creation with `503` and a reason, rather than accepted and left to fail against the service. `mineru-local` does not: a self-hosted deployment decides whether it sits behind a token at all.

## Parse Run Creation

`POST /api/v1/documents/{documentId}/parse-runs` requires an administrator, an OIDC owner/grantee with parse permission, or an API client with `parses:write`. Cookie writes require antiforgery validation.

The request may name `providerConfigId`; otherwise it uses the enabled default. No usable default returns `503`, while an explicit unknown ID returns `404`. A configuration whose type authenticates every call and holds no credential also returns `503`, under its own title, because that is the state a deployment nobody has finished setting up starts in.

Creation persists the `queued` state, Document ID, Provider type, logical configuration and immutable version IDs, non-sensitive options JSON, source/planned submitted media types, maximum attempts, caller facts, and timestamps. Maximum attempts defaults to 3 and accepts 1–10. Options must be a JSON object no larger than 16 KiB and reject credential/password/secret/token/API-key/authorization fields at any depth.

`submittedMediaType` initially equals the source. Before outbound work, the executor validates the immutable Provider capabilities and size limits. If an Office source requires PDF fallback, it saves an independent conversion snapshot and `normalized-pdf` Artifact.

Callers may send one visible-ASCII `Idempotency-Key` up to 256 characters. Scope includes subject, Document, and operation. The first request returns `201`; a replay returns the original record with `200` and `Idempotency-Replayed: true`, even if the default Provider changed. Without the header, each request creates a distinct Parse Run.

`GET /api/v1/parse-runs/{id}` requires corresponding resource access or `parses:read`. It returns stable status/stage, configuration snapshot facts, sanitized options, media types, attempt count, sanitized errors, and timestamps. It excludes leases, concurrency tokens, internal callers, credentials, checkpoints, and external task IDs.

`GET /api/v1/parse-execution` requires `parses:read` and answers `workerEnabled` and `providerCredentialMissing`. A Host started without Workers accepts Parse Runs and queues them behind nothing, and `queued` alone cannot distinguish that from a queue about to move. It is a deployment choice and not settable from a browser, so the workspace reports it rather than offering to change it. `providerCredentialMissing` covers the other reason a parse cannot start, and the one a fresh deployment starts in: the default Provider authenticates every call and has no token yet. Only an administrator can supply one, so it too is reported rather than offered — the response carries a boolean and nothing about the configuration itself.

## Cancellation

`POST /api/v1/parse-runs/{parseRunId}/cancel` requires the same authorization as creating a Parse Run for the Document, and Cookie callers supply an antiforgery token. It moves a `queued`, `claimed`, `running`, or `retry-wait` run to `cancel-requested` with one conditional update, returns `202` with the updated record, stays idempotent through completion, returns `409` for a run that already reached `succeeded` or `failed`, and returns `404` for an unknown or inaccessible run. A run with no live lease may already be `cancelled` when the response is written, so callers depend on `status` finality rather than on observing `cancel-requested`. Completion to `cancelled` is durable and happens on the owning Worker or through Parse Run maintenance. See [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md) section 13.

## Administration Area

Every field the API accepts is reachable from `/admin`: name, type, base URL, model, backend, credential, and default. The type is a fixed choice rather than typed text, because the service accepts a closed set and a typo could only ever come back as a validation error. Editing an existing configuration leaves the type read-only, matching the service's refusal to change it.

Enabled is the exception, and it is not offered as a field. Filling the form in is the administrator saying they want this Provider used, so saving enables it; asking them to confirm that with a second tick adds a way to leave a finished configuration switched off and nothing else. Whether the credential is right is not knowable from the form and is not what this decides. Retiring a Provider is still possible and is the row's own action, deliberately outside the form — and because saving re-enables, the editor says so while the Provider it is editing is disabled.

Choosing the hosted type fills the base URL with the published address of the hosted service. It has exactly one, so requiring an administrator to retype it is requiring them to get it wrong; a typed or corrected address is left alone, and switching back to the self-hosted type removes the suggestion rather than leaving a wrong address that looks deliberate. `model` and `backend` are each read by one type and ignored by the other, so the form offers only the one that has an effect and states what a blank field produces: Cloud sends the advertised default, Local sends nothing and the MinerU service decides. These come from `provider-types` rather than from the browser bundle, because the defaults belong to the adapters and a copy in the form would go on naming the old one after an adapter changed.

The credential field starts empty on an edit, because the service never sends a stored credential back. An empty field means the stored value is kept; erasing one is a separate checkbox that sends `clearCredential`.

Disabling the default configuration from its row clears its default marker in the same write, since the service refuses a configuration that is disabled and default at once. The area also reports when no enabled default exists at all: the workspace starts a parse without naming a Provider, so that deployment has a button that can only fail.

A configuration one field short of working is reported the same way. A type that authenticates every call and holds no credential is marked in its row and, when it is the enabled default, raised as a notice that says where to get a token and opens the editor on it. This is the state the shipped `official` endpoint is in until an administrator finishes it, so leaving it to look like an ordinary configured Provider would be the page's worst answer.

The area is ordered by what a new deployment still has to do. Providers come first under a **必须配置** band, because nothing parses without one. Accounts and API clients follow. Everything with a working shipped default — sign-in, storage, business database, service settings — is collected at the bottom under **可选配置** with its body collapsed, each panel showing what it currently holds so the common answer, nothing to do here, needs no clicks. A collapsed panel opens itself when what it holds is broken: a storage or sign-in section the service refused at startup, a database it cannot reach, or a setting waiting on a restart. Hiding those would be the one thing this layout must not do.

## Current Gaps

- administrator Provider connectivity testing;
- richer execution-attempt history and upstream cancellation propagation;
- deployment-specific trust policy for configurable Cloud/Local base URLs;
- broader production MinerU and LibreOffice sample coverage.

The workspace and administration area already expose document, result, Provider, and API-client management; they are not future placeholders.
