# Administration Audit

- Status: Implementation note
- Last updated: 2026-09-30

## Purpose

Management actions are recorded as an audit trail through ServiceMantle's audit domain
(`ServiceMantle.Audit` + `ServiceMantle.Persistence.Relational`). This document defines what is
recorded, where it is stored, what it never contains, and what operating it means.

## Storage

Audit rows live in the control-plane SQLite database, in the `service_audit_logs` table created by
the control-plane migration `20260930101059_AddServiceMantleManagementAudit`. They are applied by the
existing control-plane migration entry at startup and by the first step of the one-shot migration
command; no new entry point exists.

The business database cannot hold the audit trail: the library's audit mapping supports SQLite,
PostgreSQL, and SQL Server but not MySQL or MariaDB, while StructaDoc's business database must
support all four (ADR-0004). The control plane is always SQLite and already holds administrator
accounts and settings, so the audit trail joins the existing backup recovery set
(`/data/control.db`, storage, and the key ring as one unit).

## Record Points

| # | Management action | Action | Target |
|---|---|---|---|
| 1 | Administrator login (success and failure) | `admin_login.succeeded` / `admin_login.failed` | `admin_session` |
| 2 | Setup claim (first administrator created) | `structadoc.setup.claimed` | `structadoc.administrator_account` |
| 3 | Administrator account changes: create, own-password change, password reset, enable, disable, delete | `structadoc.administrator_*` | `structadoc.administrator_account` |
| 4 | API client creation and revocation | `structadoc.api_client_created` / `structadoc.api_client_revoked` | `structadoc.api_client` |
| 5 | Setting and OIDC setting writes and clears (including rejections) | `configuration.changed` | `configuration` (the key) |
| 6 | Provider configuration creation, new versions, enable, disable | `structadoc.provider_config_*` | `structadoc.provider_config` |
| 7 | System restart requests | `structadoc.restart_requested` | `service` |

The outcome of each row is the operation's real result: `Success`, `Failure`, or `Denied` (a
deployment-pinned setting write, for example, records `Denied`). The operator is the acting
administrator (`interactive_admin` source, the account's stable identifier, and its username). A
failed sign-in has no established identity, so it records the `anonymous` source with the submitted
username as its only attribution.

The one management action that happens without any operator is the setup claim: the account the
claim produced is the identity the event carries.

## Metadata Allowlists

Each record point carries an explicit, non-sensitive metadata allowlist:

- Administrator account changes: the affected account's username.
- API clients: the client's name — never its credential.
- Provider configurations: name, provider type, version number, and the resulting enabled and
  default state — never the credential or the endpoint address.
- Settings: nothing. The key name is the target identifier and the outcome is the result; the value
  never enters the record.
- Login, setup, restart: nothing.

Client IP is recorded when the peer address is a real address. Correlation identifiers are not
recorded yet; they arrive with the correlation feature of the observability track.

## Failure and Atomicity Semantics

- The audit writer stages rows and never saves on its own. Setting writes commit the audit row and
  the setting row in one `SaveChangesAsync`: both commit together and roll back together, and a
  settings write whose audit row cannot be constructed fails rather than landing unaudited.
- Record points whose own changes are in the business database (API clients, Provider
  configurations) or already committed (login, account changes, restart) persist their audit row in
  a separate control-plane save. An audit failure there is logged at error level and never
  retroactively fails an operation that already succeeded; it is never silently swallowed.
- A restart writes its audit row before scheduling the stop, so the record cannot be lost to the
  shutdown it describes.

## Sensitive Content Boundary

No audit field carries a token, password, connection string, setup code, or Provider credential.
Sensitive setting writes record only the key name; Provider audits record only configuration
identity and version. The library's sensitive-content policy is defense in depth behind the caller
allowlists: metadata keys that name a secret are rejected at event construction, and secret-shaped
values (assignments like `password=`, connection-string shapes, bearer tokens, JWTs, PEM key blocks)
are redacted from descriptions and metadata values before an event exists. Direct SQL or imports
bypassing the writer are outside the write guarantee; query-side validation re-checks legacy rows.

## Explicit Non-Guarantees and Retention

- No query API or UI exists yet; the trail is read from the database directly until a later issue
  adds one.
- There is no automatic retention or cleanup: the `service_audit_logs` table grows with management
  activity, and the operator owns that growth. A deployment with many logins and settings changes
  should include the control-plane database in capacity monitoring.
- Startup-time events (bootstrap administrator provisioning, official Provider seeding) are not
  recorded; they are deployment inputs, not interactive management actions.
- OIDC user operations are not management actions and are not recorded; resource-level operations
  (document uploads, parsing) are outside management audit entirely.
- Provider configuration deletion and API client update/rotation are not among this issue's bounded
  record points and are not recorded yet.
