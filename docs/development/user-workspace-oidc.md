# User Workspace and OIDC

StructaDoc's web interface is a user-facing product, not only an administrator console. Signed-in users can upload and filter their own documents, create Parse Runs, inspect normalized results, download originals, export results, and share document permissions. Administrators see additional Provider configuration and API-client management areas in the same application.

## Identity Boundary

- External interactive users sign in with standard OIDC Authorization Code flow and PKCE.
- The stable identity key is `(issuer, subject)`, not email, username, or a Provider-private user ID.
- `issuer` is an ASCII HTTP(S) OIDC issuer without query or fragment, up to 512 characters. `subject` follows the 255-character ASCII bound used by the implementation.
- Both identity parts compare case-sensitively. MySQL and MariaDB use `ascii_bin` so a default collation cannot merge distinct subjects.
- Authority, client, scopes, and claim/role mapping come from the generic `Oidc` configuration section.
- SignaCore can act as a compatible OIDC Provider, but StructaDoc does not reference or bind to SignaCore code or private contracts.
- The local administrator Cookie remains for initial bootstrap and break-glass access during identity-Provider outages.
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
