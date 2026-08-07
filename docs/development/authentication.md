# Authentication

- Status: Implementation note
- Last updated: 2026-08-07

## Current Boundary

Authentication follows [ADR-0005](../adr/0005-authentication-and-api-clients.md) and [ADR-0006](../adr/0006-user-workspace-and-oidc.md). Interactive OIDC users, the local break-glass administrator, and machine API clients are distinct subject types.

| Subject | Credential | Stable identity / stored verifier | Revocation |
|---|---|---|---|
| OIDC user | Host-managed HttpOnly Cookie after OIDC callback | Case-sensitive `(issuer, subject)` | External identity lifecycle plus local resource grants |
| Local administrator | HttpOnly, SameSite Strict Cookie | Upgradeable password hash, security stamp, enabled state | Disable account or change stamp |
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

## Bootstrap Administrator

The first deployment can inject:

```text
Authentication__BootstrapAdministratorEmail
Authentication__BootstrapAdministratorPassword
Authentication__BootstrapAdministratorDisplayName
```

Email and password must be configured together. Password length is 12–1024 characters. After migration, the Host creates the account only if its normalized email does not exist. Bootstrap settings never overwrite a stored password, enabled state, or security stamp. Remove the bootstrap password after first use.

## Local Administrator Session Flow

1. `GET /api/v1/admin/antiforgery` and retain its Cookie plus `requestToken`.
2. `POST /api/v1/admin/session` with email/password JSON and `X-CSRF-TOKEN`.
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

- richer administrator account operations and security audit views;
- configurable failed-login lockout and persistent authentication audit;
- production reverse-proxy, HTTPS, and Cookie Secure deployment recipes;
- an external Data Protection key-ring option for multi-instance platforms.
