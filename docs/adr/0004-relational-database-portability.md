# ADR-0004: Support Replaceable Relational Databases

- Status: Accepted
- Date: 2026-08-05
- Superseded in part by:
  [ADR-0009](./0009-canonical-persisted-actor-identity.md)

## Context

Small self-hosted installations want one application container and a persistent volume. Other installations already operate PostgreSQL, MySQL, or MariaDB. Binding the domain, migrations, or Worker claims to one database would raise adoption and maintenance costs.

The database stores ordinary business data and is also the authoritative Parse Run queue. Supporting a database therefore includes migrations, transactions, concurrent claims, leases, idempotent commits, recovery, and upgrades—not merely connectivity or table creation.

## Decision

### 1. Supported databases

- **SQLite:** lightweight deployments with one StructaDoc application instance.
- **PostgreSQL:** external database and multi-instance deployments.
- **MySQL:** external database and multi-instance deployments.
- **MariaDB:** an independent target, not an assumption based on MySQL compatibility.

Only database versions exercised by contract tests are declared supported.
MySQL and MariaDB support additionally requires the InnoDB row-format, page-size,
and migration preflight conditions defined by
[ADR-0009](./0009-canonical-persisted-actor-identity.md). Existing server deployments
can be reused only when they meet those storage requirements.

### 2. Code boundary

- Domain, Application, Contracts, and public APIs do not reference database Provider types or dialects.
- Adapters use EF Core for the shared model and ordinary CRUD.
- Configuration selects the Provider explicitly; never infer it from a connection string.
- Atomic claims and other operations that cannot be generalized reliably live behind internal dialect boundaries.
- Database differences never appear as different public status values, DTO fields, or business behavior.

### 3. Portable model

Use types, indexes, and constraints that all four databases express reliably. Do not place unadapted PostgreSQL `jsonb`, arrays, sequences, partial indexes, or other single-database features in the core model. Store bounded Provider-native structures as JSON text or Artifacts and model queryable business fields explicitly.

Store and compare time with UTC semantics. Identifiers, enums, concurrency versions, collations, index lengths, and delete behavior must map deterministically on every database. Dialect-specific optimizations may improve performance but cannot change observable behavior.

### 4. Migrations

Maintain separate, reviewable, repeatable migration sets for every database. A release must:

- migrate an empty database to the current version;
- upgrade from the previous supported release;
- fail readiness when migration fails instead of patching with ad hoc startup DDL;
- record the active Provider and migration history.

Production startup migration is an explicit deployment setting.

### 5. Concurrency and deployment

Every database follows the [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md).

- PostgreSQL, MySQL, and MariaDB support multiple StructaDoc Worker instances.
- SQLite supports controlled Worker concurrency in one application instance only.
- Multiple containers must not share one SQLite file, and SQLite must not run from NFS, SMB, or another network filesystem.
- SQLite and local Artifacts belong on persistent volumes covered by a consistent backup plan.

Dialects may use different locks or compare-and-set implementations, but contention must not duplicate execution and expired-lease recovery must produce the same result.

### 6. Verification

A shared persistence contract suite runs against SQLite, PostgreSQL, MySQL, and MariaDB and covers:

- clean migration and version upgrades;
- business-resource constraints;
- competing Worker claims without duplicate success;
- renewal, lease loss, recovery, cancellation, and retries;
- idempotency and unique-constraint races;
- UTC time, ordering, pagination, and string behavior.

SQLite tests use temporary file databases rather than `:memory:` when file locking and transactions matter. Server databases use real corresponding instances, not mocks or compatibility substitutes.

## Consequences

### Positive

- Small deployments need only one application container and volume.
- Operators can reuse existing PostgreSQL deployments and MySQL or MariaDB
  deployments that meet the declared InnoDB storage requirements.
- Domain and public contracts do not depend on one database.
- “Supported” includes reliable job behavior, not only CRUD.

### Trade-offs

- Multiple migration sets, dependencies, and integration environments require maintenance.
- The core model cannot rely unconditionally on one database's specialized types or indexes.
- Atomic claims require a dialect boundary.
- Moving from SQLite to a server database requires a controlled migration or import/export path.

## Rejected Alternatives

- **PostgreSQL only:** simplifies implementation but adds a service to the smallest deployment and excludes existing MySQL/MariaDB environments.
- **EF Core abstraction only:** ordinary CRUD is portable, but reliable claims, locks, and generated migration SQL have real differences.
- **Multi-instance shared SQLite:** SQLite is appropriate for embedded single-instance persistence, not for emulating a server database through a shared filesystem.
