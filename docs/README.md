# StructaDoc Documentation

This directory contains StructaDoc's architecture decisions, cross-component specifications, implementation notes, and deployment guidance. Documentation must distinguish target contracts from current implementation and verification facts.

## Authoritative Sources

| Subject | Authoritative source |
|---|---|
| Product position, capability summary, and entry points | [`README.md`](../README.md) |
| Repository collaboration and change rules | [`AGENTS.md`](../AGENTS.md) |
| Difficult-to-reverse architecture decisions | [`adr/`](./adr/) |
| Cross-component public contracts | [`specifications/`](./specifications/) |

## Architecture Decisions

| ADR | Status | Decision |
|---|---|---|
| [ADR-0001](./adr/0001-product-boundary.md) | Accepted | Limit the product to document ingestion and structured parsing |
| [ADR-0002](./adr/0002-parser-provider-abstraction.md) | Accepted | Adapt online and local parsers through Provider interfaces |
| [ADR-0003](./adr/0003-technology-and-single-image-deployment.md) | Accepted | Use .NET 10 and one application image containing the web UI, Workers, and LibreOffice |
| [ADR-0004](./adr/0004-relational-database-portability.md) | Accepted | Support SQLite, PostgreSQL, MySQL, and MariaDB with consistent job semantics |
| [ADR-0005](./adr/0005-authentication-and-api-clients.md) | Accepted | Separate administrator Cookie sessions, API-client keys, scopes, and antiforgery protection |
| [ADR-0006](./adr/0006-user-workspace-and-oidc.md) | Accepted | Provide a user workspace, generic OIDC, and resource-level authorization |
| [ADR-0007](./adr/0007-resource-lifecycle-and-cleanup.md) | Accepted | Use ownership, explicit sharing, and durable resource cleanup |

## Specifications

| Specification | Purpose |
|---|---|
| [Canonical Document Model](./specifications/canonical-document-model.md) | Provider-neutral structured document semantics exposed by StructaDoc |
| [Parse Job Lifecycle](./specifications/parse-job-lifecycle.md) | Persistent Parse Run states, claims, leases, retries, cancellation, and recovery |

## Implementation Notes

| Document | Subject |
|---|---|
| [Authentication](./development/authentication.md) | Local administration, generic OIDC, API keys, bootstrap, and antiforgery |
| [Canonical Result Persistence](./development/canonical-result-persistence.md) | Bundle validation, storage verification, and idempotent success transactions |
| [Continuous Integration](./development/continuous-integration.md) | Build, database contracts, production container, and browser validation |
| [Database Support](./development/database-support.md) | Providers, migrations, configuration, and verification matrix |
| [Document Reading](./development/document-reading.md) | Listing, detail, download, caching, and Range semantics |
| [File Storage](./development/file-storage.md) | Local/S3 persistence, upload validation, and conflict-safe writes |
| [MinerU HTTP Providers](./development/mineru-http-providers.md) | Cloud signed upload, Local multipart, polling, result streaming, and SSRF boundaries |
| [Office Conversion](./development/office-conversion.md) | Constrained LibreOffice execution, snapshots, and recovery |
| [Provider Config and Parse Runs](./development/provider-config-and-parse-runs.md) | Immutable Provider configuration, browser administration, and Parse Run creation |
| [Provider Execution](./development/provider-execution.md) | Provider capabilities, execution snapshots, heartbeats, and resumable orchestration |
| [Provider Result Intake](./development/provider-result-intake.md) | Idempotent ZIP storage and bounded archive validation |
| [Provider Result Normalization](./development/provider-result-normalization.md) | MinerU entry discovery, deterministic identity, and canonical mapping |
| [Result API and Resource Lifecycle](./development/result-api-and-resource-lifecycle.md) | Stable DTOs, downloads, exports, and durable deletion |
| [S3 and Large PDFs](./development/s3-and-large-pdf.md) | S3-compatible storage and resumable PDF segmentation |
| [Service Settings](./development/service-settings.md) | Browser-managed configuration including storage and the business database, precedence against the deployment, encrypted secrets, connection tests, restart, and recovery from a value that will not start |
| [User Workspace and OIDC](./development/user-workspace-oidc.md) | User-facing workspace, generic external identity, ownership, and sharing |

## Deployment

| Document | Subject |
|---|---|
| [Single Container](./deployment/single-container.md) | Building and running the Host, Vue UI, LibreOffice, fonts, and SQLite volume in one image |

## Documentation Rules

- The README introduces and links; it does not duplicate field-level contracts.
- ADRs record accepted decisions and their consequences.
- Specifications define behavior shared across components.
- Implementation notes describe current code and verification facts.
- Planning, implementation, and verification status must remain explicit.
- Database differences must not change the domain model, public API, or job lifecycle.
- Repository documentation, code comments, logs, and exceptions use English.
