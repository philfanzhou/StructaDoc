# ADR-0010: Do Not Adopt the ServiceMantle Installation and Management Model

- Status: Accepted
- Date: 2026-09-30
- References: [ADR-0001](./0001-product-boundary.md), [ADR-0005](./0005-authentication-and-api-clients.md),
  [ADR-0006](./0006-user-workspace-and-oidc.md)

## Context

StructaDoc is adopting ServiceMantle foundations incrementally: service identity and structured
logging, migration orchestration, and the management audit domain (the ServiceMantle track).
ServiceMantle also ships a complete installation and management model:

- persistent `service_installations` state with a one-time 32-character Base64URL Setup Code
  (digest storage, constant-time comparison, 5-minute-to-24-hour validity), an anonymous
  installation-status entry, `MapServiceMantleSetup` completing setup as one `{"code","input"}`
  transaction, management session login/read/logout entries, a startup phase gate over one
  `(phase, migrationStatus, databaseStatus)` snapshot, and a management identity model
  (`servicemantle.operator_id` and permission claims, `ManagementAdmin`/`ManagementSession`
  policies);
- contracts at `ServiceMantle`'s
  `docs/contracts/management-setup.md` (including the `#531` input mode), `management-session.md`,
  `management-entries.md`, `management-installation-status.md`, and `phase-admission.md`.

StructaDoc's current model is different in kind: the first visitor claims the administrator
account anonymously through `POST /api/v1/setup` (`src/StructaDoc.Host/Setup/SetupEndpoints.cs`,
backed by `IAdministratorProvisioningService`): rate-limited, antiforgery-protected, atomic
against concurrent claims via a fixed-primary-key `setup_claims` row, with the claim window made
attributable rather than closed — the source address is recorded and reported under
`/api/v1/admin/setup-claim` until an administrator acknowledges it
([Authentication](../development/authentication.md)). Deployments that cannot accept the window
provision through pinned bootstrap credentials instead, which close setup before the service
accepts requests. The administration area is StructaDoc's own cookie surface at
`/api/v1/admin/*` on the always-local SQLite control plane. The two-database design —
administration must stay reachable while the configured business database is not — is carried by
the startup sequence in `src/StructaDoc.Host/Program.cs` and
[Service Settings](../development/service-settings.md).

This ADR records the decision, evaluated as six questions.

## Decision

**Do not adopt the ServiceMantle installation and management model.** StructaDoc keeps its
first-visitor claim with the attributable-window remedy, its own administrator cookie surface, and
its data-driven setup completion. The ServiceMantle capabilities StructaDoc does adopt — service
identity, migration orchestration, and the audit domain — are orthogonal to this model and are not
affected by this decision. No implementation issues are derived from it.

### 1. Security comparison: anonymous claim vs Setup Code

The current model's window — anyone who reaches the service before the operator can claim it — is
not open-ended: the endpoint shares the administrator sign-in rate limit, requires antiforgery,
the claim is atomic against concurrent callers, and the window is *attributed* (source address
recorded and reported to administrators until acknowledged). Deployments that cannot accept the
window at all have an existing, stronger closure: pinned bootstrap credentials create the
administrator during startup and setup never opens.

The Setup Code model replaces that window with a credential the first administrator must already
hold. Delivering it means the operator reads it from the container log or a CLI surface — which
puts a secret-shaped value into the log stream, contradicting the repository's own security
boundary (credentials do not enter logs) and the sanitizing logging pipeline the observability
track installs. The code also has to reach the person who completes setup in the browser, which
assumes server access that StructaDoc's documented first-run flow deliberately does not require
(the README quick start is: start the container, open the address, the first visitor creates the
administrator). For an unattended or restricted-network deployment — the cases where the code
model shines — the pinned bootstrap credential already covers StructaDoc without a second
delivery channel.

**Conclusion:** the claim model with attribution and the bootstrap pin is the better fit for this
product's boundary; the code model's advantage (closing the window without server-side
provisioning) is already served, and its cost (a secret in logs, a delivery channel the target
operator does not have) is real. The `/setup` web page and the README first-run flow stay as they
are.

### 2. The `{"code","input"}` single-transaction model

Integration would be feasible: `MapServiceMantleSetup(SetupInputExecutor?)` hands a
consumer-defined `input` object to a consumer-owned transaction executor, and
`IAdministratorProvisioningService.ClaimFirstAdministratorAsync` is already one atomic unit
covering the password policy, the fixed-primary-key claim row, and the sign-in. The executor's
obligations (validate the code read-only before input semantics, treat input values as sensitive,
never echo them) map cleanly onto the existing service.

But the transactional property the input model adds — credentials and installation state
committing in one unit — is already guaranteed by the claim path, which writes the administrator,
the claim row, and (as of the audit work) the audit row in one `SaveChangesAsync`. What the input
model would actually add here is the *code*, which question 1 rejects. Feasible does not mean
worth the contract cost.

**Conclusion:** no integration; the existing single-transaction claim is retained.

### 3. Phase gate applicability

The gate admits management surfaces from one snapshot: `(phase, migrationStatus, databaseStatus)`.
StructaDoc has two databases whose states must be allowed to disagree: the control plane is always
a local SQLite file that is migrated before the host serves, while the business database is
whatever an administrator configured and may be absent, unreachable, or unmigratable. The product
requirement is that the administration area stays reachable *precisely when the business database
is not*, because that is the only surface from which the configuration can be corrected; the
recoverable-fault design implements it (a stored configuration that cannot be prepared is recorded
and the service starts without a usable business database, failing readiness only).

A single `databaseStatus` cannot express that split. Mapping both databases onto it either
reports the business database's failure and lets the gate 503 the management surface at the exact
moment it must be usable, or reports only the control plane and the gate degenerates into a
state the startup sequence already guarantees (setup availability is decided by data — whether an
administrator exists — and control-plane migration completes before requests are served).

**Conclusion:** the phase gate is not adopted. Its benefit (blocking management endpoints during
setup or migration) is already produced by data-driven availability, and its single-snapshot model
is actively wrong for the two-database design.

### 4. Public contract compatibility

`GET/POST /api/v1/setup` is a published v1 contract: the status shape, the claim request, the
`404` once an administrator exists, and the sign-in on claim. Adopting the setup-code completion
changes the anonymous first-run semantics (a code becomes required) — a breaking change that, by
the repository's compatibility rules, would force a contract major-version bump or a dual-track
transition. Neither is justified by questions 1–3, and a dual track would mean two privileged
anonymous endpoints where today there is one that closes itself by data.

**Conclusion:** keep the published contract. No version bump, no transition.

### 5. Management identity and session mapping

The claim types (`servicemantle.operator_id`, `servicemantle.operator_source`,
`servicemantle.permission`) map losslessly onto StructaDoc's existing principal — and indeed onto
the audit operator model the audit record point work already uses. But adopting the *session
entries* means a second login surface (`/management/v1/session/login` with its own cookie scheme
and adapter SPI) beside the published `/api/v1/admin/session`, which carries antiforgery, the
sign-in rate limit, and the SPA's login flow. Two cookie schemes and two anonymous-adjacent login
endpoints on one host is strictly more attack surface for no capability StructaDoc lacks. The
worthwhile part of this model — the operator projection — is already in use through the audit
domain, where it belongs.

**Conclusion:** no session adoption; the existing administrator session surface is retained, and
the operator identity mapping continues to serve the audit trail.

## Consequences

### Positive

- The first-run story, the published `/api/v1/setup` contract, the README quick start, and the
  web `/setup` page are all unchanged.
- No secret-shaped setup credential ever needs a log or CLI delivery channel.
- The administration area keeps its two-database failure behaviour, which a single-snapshot phase
  gate could not express.
- One anonymous privileged endpoint (self-closing by data) instead of a management prefix with
  phase admission and a second session surface.

### Trade-offs

- The first-visitor claim window remains a property of the product. It stays attributed and
  closable through bootstrap pinning, and the trade-off is now recorded here rather than only in
  the authentication notes.
- StructaDoc keeps ownership of its session, antiforgery, and rate-limiting wiring rather than
  inheriting the library's management-entry baseline; future ServiceMantle capabilities that
  assume the management surface (setting queries, management entries) will need the same
  evaluation this ADR recorded.
- The identity overlap (`servicemantle.*` claims vs StructaDoc claims) means any future management
  API work must map operators explicitly, as the audit record points already do.

### Rejected alternatives

- **Adopt the full model:** breaks the published setup contract, adds a log-delivered secret, and
  misfits the two-database availability requirement (questions 1, 3, 4).
- **Adopt the Setup Code only:** the window it closes is already closed by bootstrap pinning, and
  the delivery channel it needs conflicts with the logging security boundary (question 1).
- **Adopt the management session only:** a second privileged login surface with no new capability
  (question 5).
- **Adopt the phase gate only:** its single snapshot cannot represent control-plane/business
  disagreement, and its admitted-state table duplicates guarantees the startup sequence already
  produces (question 3).

The ServiceMantle capabilities StructaDoc did adopt — service identity and the logging pipeline,
migration orchestration under provider leases, and the management audit domain — do not depend on
the installation model, and this decision does not reopen them.
