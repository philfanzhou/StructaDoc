# ADR-0005: Separate Browser Sessions from API-Client Key Authentication

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc serves interactive browser users and other applications. Humans need sessions and resource authorization; applications need stable machine credentials with least-privilege scopes. Sharing cookies, administrator passwords, or undifferentiated tokens would increase credential exposure and privilege-escalation risk.

The design must also remain self-hostable in one image and portable across all supported databases.

## Decision

### Interactive sessions

- External users authenticate through generic OIDC as defined by [ADR-0006](./0006-user-workspace-and-oidc.md).
- A local administrator remains in `admin_users` for bootstrap and break-glass recovery.
- Local passwords use ASP.NET Core `PasswordHasher<TUser>` upgradeable one-way hashes.
- The Host establishes a dedicated HttpOnly Cookie session with a finite lifetime; OIDC tokens are not exposed to browser JavaScript.
- Local administrator principals include an ID and security stamp. Authorization checks current enabled state and stamp so account changes revoke existing sessions.
- Browser Cookie writes require antiforgery validation.
- Bootstrap credentials come only from environment variables or deployment secrets and never overwrite an existing account.

### API clients

- Machine callers are stored separately in `api_clients` and do not reuse human accounts or cookies.
- An API key contains a public client UUID and at least 256 bits of random secret material.
- The full credential is shown only at creation or rotation; the database stores only SHA-256 of the secret.
- Verification uses fixed-time comparison and checks enabled/revoked state.
- Explicit scopes include `documents:read`, `documents:write`, `parses:read`, and `parses:write`.
- Send credentials as `Authorization: ApiKey <credential>`, never in URLs, cookies, or logs.

### Authorization

- Local administrators, OIDC users, and API clients have distinct subject types and authentication schemes.
- Administrators use administrative policies; OIDC users use ownership and grants; API clients require endpoint scopes.
- Cookie writes require antiforgery tokens. API-key requests do not because browsers do not attach those credentials automatically.

### Data Protection

ASP.NET Core Data Protection protects application cookies, antiforgery tokens, Provider credentials, and resumable submission checkpoints. Persist the key ring under a fixed application name. A single instance uses a protected volume; multiple instances share the same key ring or use a future external key-management design.

## Consequences

### Positive

- Human and machine credential lifecycles, transports, and permissions evolve independently.
- A database leak does not directly reveal high-entropy API-client secrets.
- Standard Cookie, OIDC, PasswordHasher, authorization, and antiforgery facilities reduce custom security protocol.
- Authentication data uses the same portable migrations as business data.

### Trade-offs

- The frontend must refresh and send antiforgery tokens for Cookie writes.
- Prompt revocation requires an authoritative database check; any future cache must preserve a bounded revocation delay.
- Break-glass local administration still requires secure password operations and audit.
- Multi-instance browser sessions depend on a shared Data Protection key ring.

## Rejected Alternatives

- **One API key for administrators and clients:** human sessions and machine identities have different risk, revocation, and permission requirements.
- **Store recoverable encrypted API keys:** verification does not require secret recovery; one-way hashing reduces database-leak impact.
- **Bind the domain to one external identity product:** standards-based OIDC keeps identity lifecycle external without coupling StructaDoc to Provider-specific models.
