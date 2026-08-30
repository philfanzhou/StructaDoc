# Authentication

- Status: Implementation note
- Last updated: 2026-08-31

## Current Boundary

Authentication follows [ADR-0005](../adr/0005-authentication-and-api-clients.md) and [ADR-0006](../adr/0006-user-workspace-and-oidc.md). Interactive OIDC users, the local break-glass administrator, and machine API clients are distinct subject types.

| Subject | Credential | Stable identity / stored verifier | Revocation |
|---|---|---|---|
| OIDC user | Host-managed HttpOnly Cookie after OIDC callback | Case-sensitive `(issuer, subject)` | External identity lifecycle plus local resource grants |
| Local administrator | HttpOnly, SameSite Strict Cookie | Username in the control-plane database, upgradeable password hash, security stamp, enabled state | Disable account or change stamp |
| API client | `Authorization: ApiKey <credential>` | Client UUID, secret SHA-256, scopes, enabled/revoked state | Disable or revoke client |

The browser never receives OIDC access or identity tokens. Cookie and API-key validation consult authoritative local state; current revocation does not depend on cache expiry.

## Generic OIDC

OIDC uses Authorization Code flow with PKCE, standard discovery and token validation, and configurable scope and role-claim mapping. The stable authorization key is `(issuer, subject)`, not email, display name, username, or a Provider-private identifier.

Configuration is under `Oidc`:

```json
{
  "Oidc": {
    "Enabled": true,
    "Authority": "https://identity.example.com",
    "ClientId": "structadoc",
    "ClientSecret": "from-secret-store",
    "RequireHttpsMetadata": true,
    "Scopes": ["openid", "profile", "email"],
    "RoleClaimType": "role",
    "AdministratorRole": "structadoc-admin"
  }
}
```

SignaCore and other standards-compliant OIDC Providers can be configured without Provider-specific business code. Inject `ClientSecret` through a deployment secret.

## Control Plane

Administrator accounts live in a dedicated SQLite database at `ControlPlane:DatabasePath`, default `./data/control.db` and `/data/control.db` in the image. It has no provider switch and needs no configuration.

The separation is deliberate. The business database is something an administrator configures, so an administrator whose account lived there could not be used to configure it. Keeping the control plane local also means local sign-in still works while the business database is unreachable, which is when break-glass access matters.

Administrators are identified by a username, not an email address. The account is local to one deployment and never federated, so an email address would imply a mailbox the service neither verifies nor uses. A username is 3–64 characters of ASCII letters, digits, `.`, `_`, or `-`, starts and ends with a letter or digit, and is unique case-insensitively.

Deployments upgrading from the former business-database administrator table are migrated before the
business migration is allowed to remove that table. Every account, password hash, enabled state, and
security stamp is copied into the local control plane in one transaction. The old email remains a
login alias while the account receives a deterministic local username. The import is idempotent, and
an import failure stops startup rather than discarding the only administrator and reopening anonymous
first-run setup. Once any control-plane administrator exists, normal startup no longer depends on the
business database, preserving break-glass access when that database is unavailable.

## First-Run Setup

While no administrator exists, `GET /api/v1/setup` reports `setupRequired`, `GET /api/v1/session` repeats it so the web application can route without a second request, and every client route leads to `/setup`. `POST /api/v1/setup` creates the first administrator and signs it in. Once an administrator exists the endpoint returns `404` and the client route redirects away.

The endpoint is anonymous by necessity: first run has nothing to authenticate against. It requires antiforgery validation and shares the administrator sign-in rate limit. The claim is atomic against concurrent callers through a fixed-primary-key row in `setup_claims`, not through a read-then-write check, so two simultaneous claims choosing different usernames cannot both succeed.

Anyone who can reach the service before the operator does can claim it. That window is not closed, it is made attributable: the claim records its source address, and `GET /api/v1/admin/setup-claim` reports it to administrators until one confirms it through `POST /api/v1/admin/setup-claim/acknowledge`. The report is administrator-only, because the claimant address is not other users' business. Deployments that cannot accept the window should provision through configuration instead, which closes setup before the service accepts requests.

## Configured Bootstrap Administrator

Unattended deployments and CI can provision without a browser:

```text
Authentication__BootstrapAdministratorUsername
Authentication__BootstrapAdministratorPassword
Authentication__BootstrapAdministratorDisplayName
```

Username and password must be configured together. Password length is 8–1024 characters. After migration, the Host creates the account only if its normalized username does not exist, which also closes first-run setup. Bootstrap settings never overwrite a stored password, enabled state, or security stamp. Remove the bootstrap password after first use.

## Account Administration

Administrator Cookie endpoints under `/api/v1/admin/administrators` list accounts, add them, change passwords, enable and disable them, and delete them. All writes require antiforgery validation.

Every password change rotates the security stamp, which is what ends the sessions the old password opened. Changing your own password through `/me/password` requires the current one and re-issues the calling session, so the caller stays signed in while its other sessions do not. Resetting another administrator's password requires no current password, so it is refused against your own account; otherwise the current-password requirement would enforce nothing.

Two rules protect against locking a deployment out of itself:

- an administrator cannot disable or delete their own account, so a single mistaken request cannot remove the last way in;
- disabling or deleting an account is refused while it is the only active administrator.

Sequentially the first rule already covers the second: the only active administrator is always the caller. The second exists for the case the first cannot cover, two administrators removing each other at the same time, and is enforced by the condition travelling into the `UPDATE` or `DELETE` statement rather than being read first. Disabling takes effect on the account's next request, because cookie validation rejects an inactive account.

Deleting is irreversible and drops the account's history, while disabling keeps it
and can be undone. `CanonicalActor` now maps authenticated OIDC users, API clients,
and local administrators to the exact binary structured pair defined by
[ADR-0009](../adr/0009-canonical-persisted-actor-identity.md), classifying them by
`StructaDocClaimTypes.SubjectType` rather than administrator authorization. The
shared codec preserves accepted ASCII bytes including NUL, canonicalizes UUID-backed
subjects, and validates canonical, legacy, and optional-empty persistence states.

Document ingestion and access-grant writes now store `created_by_issuer` and
`created_by_subject` through that shared codec, while migrated scalar values remain
opaque strict-UTF-8 bytes in `created_by_legacy`. Document owner and access-grant
principal fields use the same BLOB/bytea/varbinary mapping, including NUL, without
changing owner-or-grant authorization. Access-grant v1 responses decode principal
bytes and project canonical actors into the existing required string fields; legacy
actors return their exact decoded former value. Parse Run actor columns remain plain
strings until the migration tracked by #36.
Deleting an account will not remove either actor representation, but it
will remove the ability to resolve who the actor was. Resolving a canonical
local-administrator subject requires its matching `admin_users` row in the control-plane
database, so the two databases must be restored as a matched set when that audit
resolution matters.

## Local Administrator Session Flow

1. `GET /api/v1/admin/antiforgery` and retain its Cookie plus `requestToken`.
2. `POST /api/v1/admin/session` with username/password JSON and `X-CSRF-TOKEN`.
3. After successful sign-in, fetch a new antiforgery token because the principal changed from anonymous to administrator.
4. Send that new token on subsequent Cookie-authenticated writes.
5. `DELETE /api/v1/admin/session` signs out and also requires antiforgery validation.

Authentication failure returns `401` without revealing whether an account is absent, disabled, or has the wrong password. API endpoints do not redirect to an HTML login page.

The login endpoint uses a fixed window per `RemoteIpAddress`, defaulting to ten attempts per minute. Configure `Authentication:LoginPermitLimit` and `Authentication:LoginRateLimitWindow`. Multi-instance limits are currently per instance.

Behind a proxy that address is the proxy's until `ReverseProxy:TrustedProxies` names it, at which point every visitor shares one bucket and ten wrong passwords from anyone lock out everyone. Cookies are issued with `CookieSecurePolicy.SameAsRequest`, which is the same statement about the same fact: a proxy that terminates TLS forwards plain HTTP, so `Secure` is set once the forwarded scheme is believed and not before. Neither is settable from a browser, because which peer may speak for the client is a property of the network the container sits in. See [Behind a Reverse Proxy](../deployment/single-container.md#behind-a-reverse-proxy).

## API-Client Credentials

Credentials contain a version, public client UUID, and 256-bit random secret. The database stores only SHA-256 of the secret. Creation and rotation responses use `Cache-Control: no-store` and are the only places that reveal a full credential.

Registered scopes are:

- `documents:read`
- `documents:write`
- `parses:read`
- `parses:write`

Scopes are independent. Provider configuration remains administrator-only.

A scope says which verbs a key may use. It says nothing about whose resources they may be used on, which is decided by ownership: a client is a workspace principal keyed by `(structadoc:api-client, <client id>)`, it owns what it uploads, and it reaches nothing else unless a grant names it. See [Resource Boundary](#resource-boundary).

Administrator Cookie endpoints under `/api/v1/admin/api-clients` list, create, update scopes, rotate keys, and irrevocably revoke clients. All writes require antiforgery validation. Names are trimmed, scopes are de-duplicated in a stable order, unknown scopes return `400`, and concurrent or terminal-state conflicts return `409`.

## Resource Boundary

Following [ADR-0008](../adr/0008-api-client-resource-isolation.md), an API client is bounded by the same owner-or-grant rule as an OIDC user rather than reaching the whole deployment.

| Subject | Reaches |
|---|---|
| Local administrator | every resource |
| OIDC user | what `(issuer, subject)` owns or was granted |
| API client | what `(structadoc:api-client, <client id>)` owns or was granted |

The published API description states this per endpoint: which scope each one needs, which permission on the Document it needs beyond that scope, and which endpoints are reachable only from a browser. See [API Description](./api-description.md).

### Document Permissions

A grant carries a set of permissions on one Document. An owner holds all of them. They are the vocabulary `POST /api/v1/documents/{id}/access-grants` accepts, and the API description names the one each operation requires.

| Permission | Admits |
|---|---|
| `read` | the Document, its original file, its Parse Runs, and everything a run produced — Pages, Blocks, Assets, Artifacts, the canonical Markdown, and the HTML preview |
| `parse` | starting a Parse Run on the Document, and cancelling one |
| `export` | the packaged export routes under `parse-runs/{parseRunId}/exports/` |
| `delete` | deleting the Document, or one of its Parse Runs |
| `share` | listing, granting, and revoking access grants on the Document |

`export` is not a confidentiality boundary: it separates the packaged deliverable from the result surface, and a grantee holding `read` without it can still obtain every byte an export would produce. See [What `export` Gates](./result-api-and-resource-lifecycle.md#what-export-gates) before relying on it to withhold content.

There is no `write`. A Document's content is the file that was uploaded and no operation modifies one in place, so a permission over that never had anything to admit or withhold. It was accepted here until it was withdrawn, and grants written in that time still carry its bit; they report the rest of what they carry and remain valid. An unknown name returns `400`, so a caller still sending `write` is told rather than quietly given a grant that means less than it reads.

`structadoc:api-client` is a reserved issuer and cannot collide with a real one: a valid OIDC issuer is an absolute `http`/`https` URI, which this is not. Documents uploaded by an API client record it as their owner, and an owner holds every document permission.

A grant may name an API client, so `POST /api/v1/documents/{id}/access-grants` accepts `structadoc:api-client` as the issuer with a client ID as the subject. That is how a Document uploaded through the browser is handed to an integration, and it is revocable and listed like every other grant. There is no principal-level grant meaning "everything this owner has"; sharing is per Document.

A resource outside the caller's boundary answers `404`, not `403`, so holding a key does not confirm which resource IDs exist.

The implemented ownership upgrade attributed existing Documents rather than
orphaning them. At that point the scalar `created_by` audit value recorded which
client uploaded each Document, and the `AttributeApiClientDocumentOwnership`
migration recovered ownership from it in all four supported databases. The #49
migration now preserves that scalar as legacy UTF-8 bytes and changes the already-
recovered owner pair only from text to the canonical binary mapping. Documents
uploaded by an administrator stay
unowned and remain administrator-only, which is what they already were.

## Data Protection

`Authentication:DataProtectionKeysPath` defaults to `./data/keys`. The key ring protects Cookies, antiforgery tokens, Provider credentials, and submission checkpoints. It must be persistent, permission-restricted, and backed up with the database. Losing it invalidates sessions and can make encrypted Provider state unrecoverable.

Multiple Host instances must share the same key ring. The current implementation uses filesystem persistence; deployments that cannot share it safely do not yet support multi-instance browser sessions. API keys are not affected by this limitation.

Provider bearer tokens remain separate from every user credential. Adapters decrypt a token only from the immutable configuration version used by a leased Parse Run and attach it only to the configured Provider API origin. Signed upload and result-CDN requests never receive that token.

## Remaining Work

- configurable failed-login lockout and persistent authentication audit;
- rate limiting on the password-change endpoint, which currently shares nothing with the sign-in limiter;
- redirecting HTTP to HTTPS and emitting HSTS, which is left to the proxy that terminates TLS;
- an external Data Protection key-ring option for multi-instance platforms. The key ring now also encrypts stored settings, so replacing it costs a stored client secret as well as every live session; see [Service Settings](./service-settings.md).
