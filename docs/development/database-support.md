# Database Support

- Status: Implementation note
- Last updated: 2026-08-25

## Purpose

This document records the implementation of [ADR-0004](../adr/0004-relational-database-portability.md). “Supported” includes real migrations, transactions, concurrency, lease recovery, and canonical commits—not merely configuration or compilation.

## Current Matrix

| Database | EF Core Provider | Migration assembly | Verification |
|---|---|---|---|
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` | `StructaDoc.Migrations.Sqlite` | File-database contracts cover migration, document queries, authentication, concurrency, Parse Run lifecycle, and canonical transactions |
| PostgreSQL 17 | `Npgsql.EntityFrameworkCore.PostgreSQL` | `StructaDoc.Migrations.PostgreSql` | Real Testcontainers contract passes in GitHub Actions |
| MySQL 8.4 | `Microting.EntityFrameworkCore.MySql` | `StructaDoc.Migrations.MySql` | Real Testcontainers contract passes in GitHub Actions |
| MariaDB 11.4 | `Microting.EntityFrameworkCore.MySql` | `StructaDoc.Migrations.MariaDb` | Independent real Testcontainers contract passes in GitHub Actions |

MySQL and MariaDB retain separate migrations and tests even though they use one EF Core Provider package.

### InnoDB storage requirements

The StructaDoc business database requires InnoDB's `DYNAMIC` row format and
`innodb_page_size` of at least 16 KiB on MySQL and MariaDB. Index-key limits are lower
with smaller pages or `COMPACT`/`REDUNDANT` rows, so those configurations are outside
the supported boundary. This qualification supersedes part of ADR-0004's general
database portability decision; see
[ADR-0009](../adr/0009-canonical-persisted-actor-identity.md)
and the cross-reference in
[ADR-0004](../adr/0004-relational-database-portability.md). See the upstream [MySQL row-format
limits](https://dev.mysql.com/doc/refman/8.4/en/innodb-row-format.html) and [MariaDB
InnoDB limitations](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-limitations).
Target behavior pending #43 and #45: both the application-managed and one-shot
external migration paths will perform this
preflight only when a pending migration creates or rebuilds an index that depends on
the larger InnoDB key limit. For an absent database it will use a server connection
with no default database selected to validate `innodb_page_size >= 16384` and
`innodb_default_row_format = DYNAMIC` before EF Core creates the database. For an
existing database it will validate the global page size and the actual `ROW_FORMAT`
of each affected existing table from `information_schema`, consulting the server
default only for a table that does not exist yet. With no relevant pending migration,
a later change to the server default will not block an already-migrated deployment.
The replacement migrations will explicitly request `ROW_FORMAT=DYNAMIC` and verify
the resulting table formats before rebuilding their indexes. This preflight and
row-format validation are required by
[ADR-0009](../adr/0009-canonical-persisted-actor-identity.md) but are not implemented
yet. At present an unsupported server can instead fail in an existing migration with
the database's raw key-too-long error.

### SQLite encoding requirement

SQLite actor-identity replacement is also target behavior pending #49, #50, #51,
#52, and #36. Those
migrations will require `PRAGMA encoding = 'UTF-8'` and strictly validate the raw
UTF-8 bytes of every affected text value before destructive DDL. A restored UTF-16
database or malformed text will fail with an actionable error rather than copying
bytes that the new binary identity mapping cannot compare. Current StructaDoc-created
SQLite databases use SQLite's UTF-8 default; the replacement migrations and their
preflight do not exist yet. See SQLite's
[`PRAGMA encoding`](https://www.sqlite.org/pragma.html#pragma_encoding) contract.

## Provider Choice

The shared model uses EF Core 10. SQLite uses Microsoft's Provider and PostgreSQL uses Npgsql.

The stable upstream `Pomelo.EntityFrameworkCore.MySql` release still targets EF Core 9. StructaDoc does not move its .NET 10 baseline back to an out-of-support EF Core major version. MySQL and MariaDB therefore use the MIT-licensed [`Microting.EntityFrameworkCore.MySql`](https://github.com/microting/Pomelo.EntityFrameworkCore.MySql), an EF Core 10 branch with explicit `MySqlServerVersion` and `MariaDbServerVersion` dialects.

That dependency is confined to Adapters and migrations. It can be replaced with a stable upstream EF Core 10 Provider without changing Domain or public APIs. `SQLitePCLRaw` is centrally pinned to a 3.x release without the previously known NuGet vulnerability; do not restore a vulnerable transitive version by disabling audit.

## Configuration

| Key | Required | Meaning |
|---|---:|---|
| `Database:Provider` | Yes | `Sqlite`, `PostgreSql`, `MySql`, or `MariaDb` |
| `Database:ConnectionString` | Yes | Connection string for the selected database |
| `Database:ServerVersion` | MySQL / MariaDB | Explicit server version; the Host does not infer it by connecting |
| `Database:ApplyMigrationsOnStartup` | Yes | Whether the Host applies the selected migration set before serving requests |

`Database:Provider`, `Database:ConnectionString`, and `Database:ServerVersion` are also settable from `/admin` and follow the precedence in [Service Settings](./service-settings.md), so a deployment can move from the bundled SQLite file to an existing server without being recreated. The connection string is stored encrypted and never sent back to a browser, because it usually carries a password. `Database:ApplyMigrationsOnStartup` is not settable: it is a deployment choice about whether the service is allowed to change a schema, not a value an administrator adjusts.

Injecting production credentials through environment variables or deployment secrets remains supported and takes precedence; a key pinned that way is reported as managed externally and cannot be written from the browser. Repository settings contain only a credential-free SQLite development default.

`POST /api/v1/admin/settings/database/test` opens a candidate database and reads its migration history before anything is saved. It creates nothing, so a connection string pointing at the wrong database does not leave StructaDoc tables behind in it, and it separates a database that is current from one that answers but still needs migrating.

Changing the database does not move anything into it. A new database is migrated at the next start and begins empty; existing documents and Parse Runs stay in the old one.

Keep SQLite on a local persistent volume. Multiple containers must not share the file, and it must not live on a network filesystem.

A connection or migration failure prevents readiness. Whether it also prevents startup depends on where the configuration came from. A database the deployment pinned still stops the service, because whoever set it has a command line and is better served by failing at once. One an administrator chose in the browser does not: the service starts, records the fault, and reports it under `/admin`, which is the only place that mistake can be corrected from. Administrator accounts and settings live in the separate control-plane database, so signing in and fixing the connection string both keep working while the business database is unreachable.

## Durable Job Stores

`IParseRunLeaseStore` is the Application-layer job boundary. Its portable EF Core implementation:

- orders due `queued` candidates by next attempt, creation time, and ID;
- uses status, due time, and concurrency version as compare-and-set conditions;
- records claimant, lease expiry, attempt, and a new concurrency version;
- renews only a matching, unexpired lease;
- returns expired unstarted claims to `queued`;
- lets one new Worker adopt an expired `running` job that has an external task ID without resubmission or attempt inflation.

`IParseRunStateStore` restricts transitions to the current lease. It persists stages, one-time external IDs, Cloud encrypted submission continuations, retry waits, and terminal failures with conditional updates.

`IParseRunExecutionContextStore` returns a snapshot only for a matching live lease. It reads the immutable Provider configuration version captured at Parse Run creation, not the administrator's current version. Credentials and continuations are decrypted only inside this boundary and never enter public DTOs.

`IParseRunConversionStore` writes an immutable conversion snapshot only during the `converting` stage. `ParseRunLeaseHeartbeat` serializes renewal with state and result writes so each operation receives the newest concurrency token.

`IParseBundleCommitStore` verifies object size and SHA-256 before a single transaction writes Pages, Blocks, Assets, Artifacts, bundle fingerprint, and `succeeded`. Same-fingerprint replay is idempotent; a stale lease, cancellation race, partial rows, or different fingerprint cannot overwrite state.

Maintenance requeues due retries and recovers expired claims. The execution Worker adopts resumable external jobs before claiming new work. A deployment with no configured Provider makes no Provider requests, because a Parse Run cannot be created without one.

## Migration Workflow

Restore the repository tool manifest:

```bash
dotnet tool restore
```

Every shared model change requires a generated and reviewed migration in all four migration projects. Design-time factories exist only for generation; their placeholder connection strings are not runtime credentials.

Target procedure pending the Document work tracked by #35, the access-grant work
tracked by #44, and the Parse Run work in #36: the actor-identity replacement migrations
defined by
[ADR-0009](../adr/0009-canonical-persisted-actor-identity.md) will require an
exclusive application-version cutover. Operators will have to stop every old
StructaDoc instance before applying those migrations and start only the new version
after they complete. This prevents an old writer from creating a legacy actor row
after the new pre-insert replay path has established that the migrated legacy set is
immutable; a rolling mixed-version deployment will not be supported for this schema
change. Those replacement migrations do not exist yet.

## Contract Tests

Ordinary test runs always exercise SQLite file-database contracts. Server-database tests use Testcontainers and run only when explicitly enabled:

```powershell
$env:STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS = '1'
dotnet test tests/StructaDoc.DatabaseContractTests/StructaDoc.DatabaseContractTests.csproj
```

The suite migrates an empty database and checks for pending migrations. It covers document pagination, local administrators, API-client scope changes and rotation, concurrency and revocation, competing Parse Run claims, lease renewal and expiry, stage and external-ID writes, conversion snapshots, resumable adoption, failure and retry transitions, cleanup lifecycle, and canonical commits.

## Remaining Verification

- upgrade migrations from each released database schema, once releases exist;
- broader stress coverage for concurrent claims and cleanup;
- documented import/export tooling for moving from SQLite to a server database.
