# ADR-0003: Use .NET 10 and One Application Image with LibreOffice

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc is a self-hosted service with relatively infrequent writes and substantially more structured-result reads. The initial product favors simple deployment, coordinated upgrades, and low operational overhead over independently scaling the API, Workers, and converter.

Clear code boundaries do not require a separate container for every responsibility. Office-to-PDF conversion only needs a constrained LibreOffice headless process; adding Python, FastAPI, an internal HTTP protocol, and another resident service would not provide enough benefit.

The service also needs long-lived DTOs, relational transactions, durable job recovery, streaming file processing, authentication, and web hosting. .NET 10 is the LTS baseline.

## Decision

### 1. Technology baseline

- Use .NET 10 and ASP.NET Core 10 for APIs, background jobs, Providers, and platform code.
- Use the configured supported relational database as the authority for business data and persistent jobs. See [ADR-0004](./0004-relational-database-portability.md).
- Use Vue 3, TypeScript, and Vite for the user workspace and administration area.
- Build the web application into static files served by ASP.NET Core; do not deploy a separate web server or frontend container.
- Prefer built-in .NET facilities for JSON, HTTP, logging, health checks, and observability before adding dependencies.

### 2. One image and one main process

The runtime image contains:

- ASP.NET Core Runtime;
- the StructaDoc Host and dependencies;
- compiled web assets;
- LibreOffice no-GUI components and fonts for supported documents.

The StructaDoc Host is the only resident main process. It hosts the web application and API, persistent Parse and Cleanup Workers, Provider adapters, and the local LibreOffice adapter.

Workers remain logical components implemented as `BackgroundService` instances. They use database claims, leases, and heartbeats rather than an in-process queue. A future deployment may run the same image in combined, API-only, or Worker-only mode without changing the domain model or public API.

### 3. Built-in Office conversion

Submit a source document directly when the Provider supports it. Otherwise, the .NET adapter starts LibreOffice directly and creates a PDF fallback. The adapter must:

- create an isolated work directory and LibreOffice user profile for each conversion;
- pass arguments without composing a shell command from user input;
- bound concurrency, execution time, input, output, and temporary disk;
- terminate the process tree on timeout or cancellation;
- validate the exit code, output presence, and PDF signature;
- clean temporary data after success, failure, or cancellation;
- keep document content, internal paths, and sensitive names out of logs.

The converted file is a `normalized-pdf` Artifact and never overwrites the original. The Artifact and Parse Run record converter version, source and submitted formats, size, and hash.

### 4. Build and runtime boundary

The image uses multi-stage builds:

1. Node.js builds the web workspace;
2. the .NET SDK publishes the Host;
3. the final ASP.NET Core runtime stage installs LibreOffice and fonts and copies both outputs.

Node.js, the .NET SDK, and Python are absent from the runtime image.

### 5. External state

One application image does not mean that a database server runs inside it:

- SQLite uses a persistent local volume;
- PostgreSQL, MySQL, and MariaDB run as external services or official database containers;
- local file storage uses a mounted volume;
- S3-compatible object storage is an optional external dependency;
- StructaDoc never starts or manages a database server inside its image.

The minimum topology is one StructaDoc container with a SQLite volume. Multi-instance deployments connect one or more StructaDoc containers to a supported server database.

## Consequences

### Positive

- The UI, API, Workers, and conversion capability ship and upgrade as one versioned image.
- No Python runtime, conversion HTTP protocol, or extra resident process is required.
- Logical component boundaries still permit future role-based scaling with the same image.
- Database leases give single- and multi-instance deployments the same reliability semantics.

### Trade-offs

- LibreOffice and fonts significantly increase image size.
- The API, Workers, and converter share CPU, memory, and a failure domain by default.
- LibreOffice cannot be upgraded or scaled independently without revisiting this decision.
- The LibreOffice layer can make image builds slower and should use stable layers and caching.

## Rejected Alternatives

- **Separate Python converter container:** adds runtime, protocol, discovery, and operational cost for a thin LibreOffice wrapper.
- **.NET and Python web services in one container:** still requires supervising multiple resident processes and internal ports.
- **Separate API, Worker, and converter images initially:** current load does not justify independent deployment complexity.
- **Database server inside the application image:** database backup, recovery, upgrades, and lifecycle must remain separate.
- **Go as the core language:** its image and startup advantages do not outweigh .NET's fit for evolving contracts, authentication, relational transactions, and durable jobs in this project.
