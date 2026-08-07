# ADR-0006: User workspace and generic OIDC authentication

- Status: Accepted
- Date: 2026-08-07

## Decision

StructaDoc is a user-facing document workspace as well as an administrative
console. Interactive users authenticate through a configurable OpenID Connect
provider. StructaDoc depends only on OIDC discovery, authorization-code flow,
PKCE, standard token validation, and configurable claim mappings. It does not
reference a provider-specific SDK or HTTP contract.

The stable external identity key is the pair `(issuer, subject)`. StructaDoc
owns document authorization, sharing, API-client scopes, and audit facts; the
identity provider owns accounts, credentials, sign-in methods, tokens, and
identity lifecycle.

OIDC tokens are handled by the Host and are not exposed to browser JavaScript.
The Host establishes an encrypted HttpOnly cookie session after the OIDC
callback. The existing local administrator is retained as an optional
break-glass recovery identity. API clients remain a separate machine subject.

## Authorization model

- Administrators may manage providers, API clients, and system-wide resources.
- Interactive users may access documents they own or documents explicitly
  shared with their `(issuer, subject)` identity.
- API clients continue to use explicit scopes and are treated as trusted
  service principals within those scopes.
- Every document, parse result, asset, artifact, export, and deletion operation
  performs resource-level authorization in addition to endpoint policy checks.

## Consequences

Deployments may use SignaCore, Keycloak, Authentik, Entra ID, or another
standards-compliant provider without changing StructaDoc business code.
Provider-specific claims require configuration, not conditional application
logic.
