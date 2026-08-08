# User Workspace and OIDC

StructaDoc's web interface is a user-facing product, not only an administrator console. Signed-in users can upload and filter their own documents, create Parse Runs, inspect normalized results, download originals, export results, and share document permissions. Administrators additionally configure Providers and manage API clients.

## Route Layout

One Host serves both audiences and the API on a single address, as required by [ADR-0003](../adr/0003-technology-and-single-image-deployment.md):

```text
/               the document workspace
/setup          first-run administrator creation, only while none exists
/signin         workspace sign-in
/admin          the administration area
/admin/signin   administrator sign-in, including local break-glass access
/api/v1/...     the service API
```

A path prefix is not an access boundary. The web application is public static content that any visitor can download, so `/admin` protects nothing by itself; every administrative route is enforced by the administrator policy on the server. The split exists so the two audiences get their own entry point and so the administration bundle is a lazily loaded chunk that a workspace-only visitor never downloads.

Until the first administrator exists, every client route leads to `/setup`; afterwards `/setup` redirects away and its endpoint returns `404`. See [Authentication](./authentication.md) for the claim window and the administrator-only warning that compensates for it.

Local administrator credentials are entered only at `/admin/signin`. The workspace sign-in page offers OIDC alone, and points administrators at the administration entry point when OIDC is disabled.

Because the workspace and administration areas are client-side routes, the Host returns the application shell for unmatched navigation paths. `/api` and `/health` paths are excluded from that fallback so a mistyped route fails as an API call instead of answering `200` with HTML.

## Identity Boundary

- External interactive users sign in with standard OIDC Authorization Code flow and PKCE.
- The stable identity key is `(issuer, subject)`, not email, username, or a Provider-private user ID.
- `issuer` is an ASCII HTTP(S) OIDC issuer without query or fragment, up to 512 characters. `subject` follows the 255-character ASCII bound used by the implementation.
- Both identity parts compare case-sensitively. MySQL and MariaDB use `ascii_bin` so a default collation cannot merge distinct subjects.
- Authority, client, scopes, and claim/role mapping come from the generic `Oidc` configuration section.
- SignaCore can act as a compatible OIDC Provider, but StructaDoc does not reference or bind to SignaCore code or private contracts.
- The local administrator is a username-based account in a separate local control-plane database, kept for first-run setup and break-glass access during identity-Provider outages.
- API clients retain independent keys and scopes and never reuse browser cookies.

The Host handles OIDC tokens and creates an encrypted HttpOnly application session after callback. Browser JavaScript does not receive the tokens.

## Ownership and Sharing

An OIDC-created document records its owner. Owners have full document permission. Explicit grants target another `(issuer, subject)` and contain a subset of:

- `read`
- `write`
- `parse`
- `export`
- `delete`
- `share`

Every document, Parse Run, Page, Block, Asset, Artifact, Markdown view, export, share operation, and deletion performs resource-level authorization. Administrators and scoped service clients retain their separate global service policies.

## Configuration

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

Inject the production client secret through environment variables or a deployment secret. Do not commit it. HTTPS metadata should remain required outside explicitly isolated development environments.
