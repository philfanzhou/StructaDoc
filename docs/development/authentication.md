# Authentication

- Status: Implementation note
- Last updated: 2026-08-07

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

Username and password must be configured together. Password length is 12–1024 characters. After migration, the Host creates the account only if its normalized username does not exist, which also closes first-run setup. Bootstrap settings never overwrite a stored password, enabled state, or security stamp. Remove the bootstrap password after first use.

## Local Administrator Session Flow

1. `GET /api/v1/admin/antiforgery` and retain its Cookie plus `requestToken`.
2. `POST /api/v1/admin/session` with username/password JSON and `X-CSRF-TOKEN`.
3. After successful sign-in, fetch a new antiforgery token because the principal changed from anonymous to administrator.
4. Send that new token on subsequent Cookie-authenticated writes.
5. `DELETE /api/v1/admin/session` signs out and also requires antiforgery validation.

Authentication failure returns `401` without revealing whether an account is absent, disabled, or has the wrong password. API endpoints do not redirect to an HTML login page.

The login endpoint uses a fixed window per `RemoteIpAddress`, defaulting to ten attempts per minute. Configure `Authentication:LoginPermitLimit` and `Authentication:LoginRateLimitWindow`. A reverse proxy must restrict trusted forwarded headers; multi-instance limits are currently per instance.

## API-Client Credentials

Credentials contain a version, public client UUID, and 256-bit random secret. The database stores only SHA-256 of the secret. Creation and rotation responses use `Cache-Control: no-store` and are the only places that reveal a full credential.

Registered scopes are:

- `documents:read`
- `documents:write`
- `parses:read`
- `parses:write`

Scopes are independent. Provider configuration remains administrator-only.

Administrator Cookie endpoints under `/api/v1/admin/api-clients` list, create, update scopes, rotate keys, and irrevocably revoke clients. All writes require antiforgery validation. Names are trimmed, scopes are de-duplicated in a stable order, unknown scopes return `400`, and concurrent or terminal-state conflicts return `409`.

## Data Protection

`Authentication:DataProtectionKeysPath` defaults to `./data/keys`. The key ring protects Cookies, antiforgery tokens, Provider credentials, and submission checkpoints. It must be persistent, permission-restricted, and backed up with the database. Losing it invalidates sessions and can make encrypted Provider state unrecoverable.

Multiple Host instances must share the same key ring. The current implementation uses filesystem persistence; deployments that cannot share it safely do not yet support multi-instance browser sessions. API keys are not affected by this limitation.

Provider bearer tokens remain separate from every user credential. Adapters decrypt a token only from the immutable configuration version used by a leased Parse Run and attach it only to the configured Provider API origin. Signed upload and result-CDN requests never receive that token.

## Remaining Work

- administrator account management: password change, additional accounts, and disabling;
- configurable failed-login lockout and persistent authentication audit;
- production reverse-proxy, HTTPS, and Cookie Secure deployment recipes;
- an external Data Protection key-ring option for multi-instance platforms.
