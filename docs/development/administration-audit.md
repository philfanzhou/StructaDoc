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

## Query API

The trail is read through `GET /api/v1/admin/audit`, a read-only administration endpoint (v1,
additive) behind the administrator authorization policy — an API client credential is refused, and
a GET carries no antiforgery requirement, like every other administration read.

Filters: `action`, `targetType`, `operator` (the operator identifier), and a UTC time range
`from`/`to` of at most 366 days. Entries are returned newest first. Pagination is cursor-based:
`pageSize` defaults to 50 and accepts 1 through 200; the first page is `page=1` without a cursor,
and each response's `continuationCursor` is passed back unchanged with the same filters, the same
page size, and `page` set to the returned page plus one. The cursor is opaque and bound to the
filters, the page size, and the next page, so reusing it with a different query is refused with a
stable error code rather than silently reinterpreted. `totalCount` is the count observed while the
query executed, not a snapshot: rows written concurrently may make it drift between pages, and a
backfilled row whose ordering position lies after the cursor can still appear on a later page.

The response entries carry the record identifier, the action, the target type and identifier, the
operator source and identifier, the outcome, the UTC timestamp, and the bounded description —
nothing else. The query boundary re-validates every page, so a legacy row with over-limit text
fails that page whole with `audit.entity_invalid` rather than being returned partially. A query an
administrator composed wrongly — an unknown action or target type, an out-of-range page or page
size, an impossible time range, or a cursor that does not belong to this query — is refused with
`400` and the library's stable error code in the body.

## Explicit Non-Guarantees and Retention

- There is no query UI yet; the trail is read through the API until a later issue adds one.
- There is no automatic retention or cleanup: the `service_audit_logs` table grows with management
  activity, and the operator owns that growth. A deployment with many logins and settings changes
  should include the control-plane database in capacity monitoring.
- Startup-time events (bootstrap administrator provisioning, official Provider seeding) are not
  recorded; they are deployment inputs, not interactive management actions.
- OIDC user operations are not management actions and are not recorded; resource-level operations
  (document uploads, parsing) are outside management audit entirely.
- Provider configuration deletion and API client update/rotation are not among this issue's bounded
  record points and are not recorded yet.
