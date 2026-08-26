# StructaDoc Agent Contract

## 1. Authority

- The user's current request takes precedence over the default workflow in this file.
- This file defines repository-wide collaboration and change rules.
- `README.md` defines the product position, public boundary, and project entry points.
- `docs/README.md` is the index for design decisions and specifications.
- Accepted ADRs define architectural decisions that are difficult to reverse.
- `docs/specifications/` defines target contracts shared across components.

## 2. Read Before Changing

1. Read `README.md` and confirm that the task is within StructaDoc's product boundary.
2. Read `docs/README.md` and locate the relevant ADRs and specifications.
3. For public structured data changes, read `docs/specifications/canonical-document-model.md`.
4. For Parse Run, Worker, retry, or recovery changes, read `docs/specifications/parse-job-lifecycle.md`.
5. Read the target code, tests, configuration, and deployment files before making changes.

If an expected entry point is missing, locate the actual structure and report it. Do not invent files, behavior, or completion status.

## 3. Truth Model

- Before a feature is implemented, the README, accepted ADRs, and specifications describe target behavior (to-be).
- Code, tests, configuration, migrations, and deployment files describe current implementation facts (as-is).
- When a specification and implementation conflict, determine whether implementation is incomplete, documentation is stale, or requirements changed.
- Never describe planned endpoints, tables, deployment commands, or tests as available.

## 4. Product Boundary

- StructaDoc owns document ingestion, asynchronous parsing, result normalization, structured persistence, and versioned API output.
- StructaDoc does not include full-text search, vector search, embeddings, RAG, or domain-specific data generation.
- Consumers use the public API and do not directly access the StructaDoc database or object storage.
- Interactive browser sessions and application API-client credentials remain separate.
- Administrators configure parsing providers; regular uploaders and API clients cannot modify provider configuration.

Features outside this boundary require user confirmation and a new ADR that changes the boundary before implementation.

## 5. Architecture Rules

- MinerU Cloud, MinerU Local, and future parsers integrate through the Provider abstraction.
- The public API must not expose a Provider task protocol or use raw Provider JSON as a stable contract.
- Every Provider result is normalized to `canonical-document-model.md`.
- Every Parse Run records the Provider, configuration version, and parsing-option snapshot.
- Provider configuration versions are immutable. A version referenced by a non-final Parse Run cannot be deleted or made unusable.
- Temporary external Provider state is not authoritative StructaDoc state.
- Original uploads are retained. Converted PDFs are separate Artifacts and never replace originals.
- Submit the source format when the Provider supports it; otherwise use the image's LibreOffice conversion fallback.
- Store large files and raw parse results in local or S3-compatible storage. Store business metadata, structured fields, and storage references in the database.
- Business persistence supports SQLite, PostgreSQL, MySQL, and MariaDB. Domain, application, and public API layers must not depend on one database dialect.
- Workers atomically claim work, maintain leases, and recover from crashes on every supported database. SQLite supports one application instance; server databases support multiple Worker instances.

## 6. Public Contract and Compatibility

- Public HTTP APIs use versioned paths.
- Breaking changes to public DTOs, status values, Block types, or coordinate semantics require a contract major-version change.
- Within one major version, prefer additive optional fields.
- API clients must tolerate unknown fields and Block types.
- Provider-native fields belong only in explicitly unstable extensions or Raw Artifacts.
- Internal database and storage references must not appear in public API fields.

## 7. Security

- Never commit real tokens, passwords, connection strings, or private document samples.
- Provider tokens and storage credentials must not be returned to browsers or written to logs.
- Provider credentials stored in the database must be encrypted; inject the master key through environment variables or deployment secrets.
- Online Providers transfer data externally; the UI and documentation must make that clear.
- Upload validation must not trust client MIME types alone. Limit size, processing time, memory, and temporary disk usage.
- Protect external URLs, presigned URLs, and callbacks against SSRF; use short lifetimes and least privilege.

## 8. Documentation Impact

ADRs record what was chosen, why it was chosen, and the governing constraints.
Migration implementation belongs in implementation issues; ADRs do not include
provider-specific SQL, numbered migration steps, or test checklists.

Update authoritative documentation in the same change whenever changing:

- the product boundary or explicit non-goals;
- the public API, state machine, or canonical model;
- Provider interfaces or capability semantics;
- data ownership, Artifact retention, or deletion rules;
- authentication, credentials, external transfer, or security boundaries;
- deployment dependencies or critical operational procedures.

Internal refactoring, formatting, local renames, and behavior-preserving test additions do not normally require new design documentation.

## 9. Coding and Dependencies

- Follow the repository's established stack, structure, test tools, and dependencies.
- Before adding a dependency, verify that existing code and platform capabilities do not provide an equivalent solution.
- Behavioral changes and bug fixes require automated tests proportionate to their risk.
- Database changes use reviewable, repeatable migrations rather than ad hoc startup DDL.
- Maintain independently executable migrations for every supported database and run the same persistence and lifecycle contract tests against each.
- Provider-specific code must not leak into Domain or public DTOs.
- Logs, exceptions, comments, and repository documentation use English. End-user UI text follows the product localization policy.

## 10. Verification and Safety

- Read-only analysis tasks must not make changes.
- Preserve unrelated user changes. Do not revert, overwrite, commit, or push them.
- Verify exact targets before deleting, moving, or bulk-rewriting files.
- Report truthfully which builds and tests ran, which did not, and why failures occurred.
- Before committing, check documentation links, formatting, secrets, and repository status.
