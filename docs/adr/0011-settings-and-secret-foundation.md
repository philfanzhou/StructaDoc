# ADR-0011: Keep the Control-Plane Settings and Secret Foundation

- Status: Accepted
- Date: 2026-09-30
- References: [ADR-0004](./0004-relational-database-portability.md),
  [ADR-0005](./0005-authentication-and-api-clients.md),
  [ADR-0010](./0010-setup-and-management-model.md),
  [Database Support](../development/database-support.md) (multi-instance control-plane boundary),
  [Service Settings](../development/service-settings.md)

## Context

StructaDoc's settings and secret infrastructure today:

- one key/value row per setting in the control-plane SQLite database, written one key at a time
  from `/admin`, with an `externally-managed` refusal (`409`) for keys the deployment pins, an
  absent row meaning the shipped default, and the whole row deleted on clear;
- `StructaDocSettingsConfiguration` layering deployment pins above stored rows above the
  container's defaults, reading everything before any options bind — the control plane must be
  readable before the business database it configures is reachable;
- recoverable sections: a stored section that cannot be used at startup is dropped whole, the
  service starts on the non-stored values, and the fault is reported under `/admin`;
- `isPendingRestart`, computed per key from what this process bound at startup against what is in
  force now;
- secrets encrypted through ASP.NET Core Data Protection with the file key ring under
  `/data/keys` (`StructaDocKeyRing`, fixed application name);
- `SettingCatalog` as the settable allowlist, held by architecture tests
  (`SettingCatalogContractTests`: restated defaults match the options classes, closed sets match
  what the options accept, keyring-locating keys are not settable, every recoverable section is
  one the catalog can write to).

ServiceMantle's model: a `service_settings` aggregate row per service with a monotonic version,
transactional batch updates through `ServiceSettingUpdateService` (1–64 changes, expected
version, `service_settings.version_conflict` on stale writes, key-only audit per changed key),
typed setting snapshots with full-set validation and atomic publication, sensitive values in
`sm:v1:` envelopes decrypted through an `IServiceSettingRootKeySource` — normally the instance's
Bootstrap MasterKey — and optional Data Protection key persistence into the consuming database as
`service_data_protection_keys` envelopes via `PersistKeysToServiceMantleEfCore`. Those tables are
designed to live in the consuming service's business database, travelling with
`service_installations`.

The multi-instance boundary decided in [Database Support](../development/database-support.md)
(after #95) is an input here: the control plane — where StructaDoc's settings live — is
per-instance, and a multi-instance deployment pins shared configuration identically on every
container instead of sharing it through the browser.

## Decision

**Do not migrate the settings infrastructure.** `SettingCatalog`, the control-plane key/value
rows, single-key writes with the externally-managed refusal, recoverable sections, and the
file-based Data Protection key ring all stay. **Partially adopt one capability as future work:**
database-persisted Data Protection keys for multi-instance server-database deployments, tracked
as a follow-up issue with its own compatibility strategy; nothing else is adopted.

### 1. Model mapping

`SettingCatalog` definitions map onto `IServiceSettingDefinitionProvider` mechanically (key,
type, required, sensitivity, default, restart), and StructaDoc's closed sets and bounds fit the
library's "product constraints belong to the definition registry" rule. What does not map is
everything around it:

- the **layered precedence with pin exclusion** — a deployment-pinned key never enters the stored
  layer and answers `409` in the browser — is the core of StructaDoc's settings contract; the
  library has no notion of an externally-managed key, so the exclusion would live on as custom
  wrapping code on top of the aggregate row;
- **batch atomicity and version conflict** buy coordination between concurrent administrators on
  *shared* state. StructaDoc's settings are per-instance control-plane state, one administrator
  surface, one key per request; the current single-key write already commits atomically with its
  audit row. The conflict protection would add a version field to the published API for a
  concurrency problem this product's deployment shape does not have;
- **recoverable sections** are a deliberate per-section fallback with an operator-visible fault;
  the library's snapshot model instead keeps the previous good snapshot on a failed refresh.
  Porting the drop-and-report semantics would mean re-deriving them outside the library anyway;
- **`isPendingRestart`** compares what this process bound at startup with what is in force now;
  the library's snapshot accessor exposes the current snapshot only, so this stays consumer-owned
  regardless.

**Conclusion:** the mapping is possible but absorbs the parts that make StructaDoc's settings
what they are; the library's additions are aimed at shared, multi-operator setting state.

### 2. Root key and Bootstrap

The library's sensitive-setting encryption requires a root key through
`IServiceSettingRootKeySource`, normally the MasterKey inside the instance-local Bootstrap file —
a file that also carries the database connection string. StructaDoc's deployment model is
environment-variable injection and the file key ring; a plaintext MasterKey file under the data
volume would co-locate the key with what it protects, and the Bootstrap file itself was already
evaluated and declined in [ADR-0010](./0010-setup-and-management-model.md).

A root key *without* the Bootstrap file is feasible — an environment-injected secret behind
`IServiceSettingRootKeySource` — but it only pays off if the settings migrate, and question 1
concluded they should not. The deeper ordering fact is decisive on its own: StructaDoc decrypts
stored settings (including the business database connection string) *before* any database is
reachable, which is why settings live in the always-local control plane. The library's setting
persistence is designed for the business database, where `service_installations` lives; moving
the aggregate row into the control plane is technically possible (the mapping is
provider-agnostic), but then it is a per-instance row behind a single administrator surface, and
the versioned-batch value collapses to nothing.

**Conclusion:** no Bootstrap file, no root-key migration for settings. Environment-injected
secrets remain the deployment boundary for keys.

### 3. Data Protection keys in the database

This is the one capability with value the current design lacks: `service_data_protection_keys`
would make the key ring shared through the business database, so cookies and encrypted Provider
credentials decrypt on every instance. Today a multi-instance deployment must share the file key
ring through a shared volume (ADR-0005), which is workable on one host but not across hosts, and
a Parse Run submitted on one instance is executed by a Worker that must be able to decrypt the
credential snapshot it captured — so the shared key ring is already a hard requirement of the
worker-replica form.

The startup-ordering constraint decides the boundary: the key ring must exist before stored
settings decrypt, and the stored connection string may itself be encrypted with it — a
database-backed key ring would create a bootstrap cycle. That cycle disappears exactly when the
multi-instance form already pins the business database connection string through the deployment,
which is what the multi-instance boundary documented after #95 prescribes. So the capability is
sound under its precondition: multi-instance, server database, pinned connection string, root key
injected through the environment — no Bootstrap file needed, since the repository takes a root
key accessor directly.

**Conclusion:** adopt as future work, scoped to that deployment form, with the single-container
form keeping the file key ring. Tracked as a follow-up issue; see below.

### 4. Overlap with the management audit record points

`ServiceSettingUpdateService` writes key-only audit natively. The audit work (#104) already
records every setting write and clear as `configuration.changed` with the key as the target,
staged into the same control-plane unit of work as the write. Migrating the settings
infrastructure would therefore double-write the same record point. Since question 1 keeps the
current write path, no convergence is needed: the existing record point remains the single
writer, and any future settings migration must retire it as part of that migration, not
alongside it.

**Conclusion:** no change; the overlap resolves by not migrating.

### 5. Migration path and compatibility

A migration would have to move the control-plane `settings` rows into an aggregate-row shape,
re-encrypt every secret from the Data Protection envelope to the `sm:v1:` envelope under a new
root key, and re-issue the published `/api/v1/admin/settings` contract — whose single-key
`PUT`, `externally-managed` refusal, and per-key `isPendingRestart` fields are load-bearing for
the SPA — as batch/version-aware operations. That is a contract major-version event for an
audience of one administrator surface per instance. The architecture constraints
(`SettingCatalogContractTests`) would need library-shaped equivalents for the parts the library
does not model at all (pin exclusion, recoverable sections, keyring-locating keys not settable).

**Conclusion:** the compatibility cost is not justified by any capability gained.

### 6. Outcome

- **Settings infrastructure:** do not migrate. `SettingCatalog`, control-plane key/value rows,
  single-key writes, recoverable sections, and the file key ring are retained.
- **Secrets for the settings layer:** do not migrate. No Bootstrap file, no MasterKey; secrets
  keep the Data Protection envelopes under the existing key ring.
- **Data Protection keys in the database:** adopt later, for multi-instance server-database
  deployments only, tracked as
  [#114](https://github.com/philfanzhou/StructaDoc/issues/114) with its own independently
  mergeable scope (business-database migration for `service_data_protection_keys`, root key from
  the environment, single-container deployments unchanged, no settings or management-API
  changes).

## Consequences

### Positive

- The two-database design keeps its load order: settings decrypt before any business database
  exists, and the control plane stays the only pre-business-configuration store.
- The published settings API, the recoverable-section semantics, and the pin-exclusion contract
  are unchanged, with their architecture tests still meaningful as written.
- No plaintext MasterKey file is introduced, and no secret moves onto the volume it encrypts.

### Trade-offs

- Concurrent browser edits to the same setting remain last-write-wins; the version-conflict
  protection the library offers is declined with the aggregate-row model it belongs to.
- Multi-instance deployments keep needing a shared key ring until the database-persisted key work
  lands; until then the documented answer is the shared volume, which is single-host only.
- StructaDoc keeps owning the settings stack rather than inheriting the library's snapshot
  machinery; future ServiceMantle setting capabilities will need the same evaluation this ADR
  recorded.

### Rejected alternatives

- **Full migration to `service_settings` + typed snapshots:** moves settings toward the business
  database the control plane must precede, or onto a per-instance row that erases the value;
  costs a contract major version and re-derivation of pin exclusion, recoverable sections, and
  pending-restart semantics (questions 1, 2, 5).
- **Root key through a Bootstrap MasterKey file:** co-locates the key with the data it protects
  and re-opens the Bootstrap evaluation ADR-0010 declined (question 2).
- **Migrating settings to gain the library's native audit:** double-writes the record point the
  audit work already owns (question 4).
