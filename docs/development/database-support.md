# Database Support

- Status: Implementation note
- Last updated: 2026-08-07

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

Inject production credentials through environment variables or deployment secrets. Repository settings contain only a credential-free SQLite development default.

Keep SQLite on a local persistent volume. Multiple containers must not share the file, and it must not live on a network filesystem. A server-database connection or migration failure prevents readiness.

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

Maintenance requeues due retries and recovers expired claims. The execution Worker adopts resumable external jobs before claiming new work. `Worker:ExecutionEnabled` is `false` by default, so an unconfigured deployment makes no Provider requests.

## Migration Workflow

Restore the repository tool manifest:

```bash
dotnet tool restore
```

Every shared model change requires a generated and reviewed migration in all four migration projects. Design-time factories exist only for generation; their placeholder connection strings are not runtime credentials.

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
