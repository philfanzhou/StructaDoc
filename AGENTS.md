# StructaDoc Collaboration Rules

StructaDoc is a self-hostable document service responsible for ingesting documents,
parsing them asynchronously, normalizing results, persisting structured data, and
serving versioned API output.

## Maintenance model and rule precedence

- This file is the single source of truth for constraints inside this repository.
- Codex reads this file directly; Claude Code imports it through the root `CLAUDE.md`.
- This file and the task/PR templates under `.github` jointly implement the unified
  project rules; when changing the project delivery policy, update the corresponding
  contributor documents in the same change.
- When changing general rules, change only this file. Unless a rule genuinely applies to
  a single tool only, do not put rule text into `CLAUDE.md` or other tool entry files,
  to avoid content drift or conflicts.
- `README.md` defines the product position, the public boundary, and the project entry
  points.
- `docs/README.md` is the index of design decisions and specifications.
- Accepted ADRs define architecture decisions that are difficult to reverse.
- `docs/specifications/` defines target contracts shared across components.

## Documentation language

- **English is the repository's unified language.** Repository workflow and constraint
  documents — this file, the issue templates under `.github/ISSUE_TEMPLATE/`, and
  `.github/pull_request_template.md` — are written in English.
- **Issues and PRs submitted to GitHub use English for both titles and bodies.** PR
  titles keep the English conventional commit format (`feat:` / `fix:` / `docs:` etc.),
  because after the squash merge the PR title becomes the title of the corresponding
  commit on `main`.
- **Review is conducted in English throughout**: PR inline comments, review summaries,
  replies, and reports to the user about problems found in review. Quoted code,
  identifiers, compiler diagnostic codes, and command lines stay as-is.
- **Product and technical documentation uses English**: `README.md`, `docs/`, code
  comments, logs, exception messages, and text addressed to API consumers are public or
  engineering deliverables and stay in English. End-user interface text follows the
  product localization strategy.
- Issue/PR bodies, review comments, and work reports use everyday English: prefer
  stating the concrete object, behavior, and result, and avoid internal process jargon
  and stiff, mechanical phrasing. When a necessary technical term first appears, add one
  sentence explaining its role as the reader needs it; protocol fields and precise
  technical names may stay as-is.
- Code identifiers and commit messages stay in English.

## Branching and merge policy

- `main` accepts only changes merged through Pull Requests. Do not commit directly to
  `main` locally, and do not push directly to `origin/main`; even if account permissions
  or remote configuration allow it, do not bypass the PR.
- A PR may be merged only after it has completed the applicable verification and
  satisfied the branch protection requirements. The repository uses squash merge; the
  PR title becomes the commit title on `main`.
- `main` must have remote branch protection that requires PRs and applies to
  administrators. Do not use administrator bypass, force pushes, or temporarily
  disabling protection to circumvent this section.

## Required reading before changes

1. Read `README.md` and confirm the task is inside StructaDoc's product boundary.
2. Read `docs/README.md` to locate the relevant ADRs, specifications, and implementation
   notes.
3. When changing public structured data, read
   `docs/specifications/canonical-document-model.md`.
4. When changing Parse Run, Worker, retry, or recovery logic, read
   `docs/specifications/parse-job-lifecycle.md`.

If an expected entry point does not exist, find out the actual structure and report it
honestly. Never fabricate files, behavior, or completion status.

## Target behavior vs current implementation

- Before a feature is implemented, `README.md`, accepted ADRs, and specifications
  describe the target behavior (to-be).
- Code, tests, configuration, migrations, and deployment files describe the facts of the
  current implementation (as-is).
- When a specification and the implementation conflict, determine whether the
  implementation is unfinished, the documentation is outdated, or the requirement has
  changed.
- Never describe planned endpoints, database tables, deployment commands, or tests as
  already available.

## Product boundary

- StructaDoc is responsible for ingesting documents, parsing them asynchronously,
  normalizing results, persisting structured data, and serving versioned API output.
- StructaDoc does not include full-text search, vector search, embeddings, RAG, or
  domain data generation.
- Callers use the service through the public API and do not access StructaDoc's database
  or object storage directly.
- Interactive browser sessions and application API-client credentials must remain
  separate from each other.
- Parsing Providers are configured by administrators; ordinary uploaders and API clients
  cannot modify Provider configuration.

`docs/adr/0013-non-archiving-layout-detection.md` (ADR-0013) records the single accepted
exception: an authenticated, non-archiving image layout-detection mode that creates and
retains no application data about its requests.

A feature beyond this boundary must first be confirmed by the user and covered by a new
ADR that changes the product boundary, before it can be implemented.

## Architecture constraints

- MinerU Cloud, MinerU Local, and future parsers must be integrated through the Provider
  abstraction.
- The public API must not expose Provider task protocols, nor treat raw Provider JSON as
  a stable contract.
- Every Provider result must be normalized into the model defined by
  `docs/specifications/canonical-document-model.md`.
- Every Parse Run must record a snapshot of the Provider, the configuration version, and
  the parsing options.
- Provider configuration versions are immutable. A version referenced by a non-terminal
  Parse Run must not be deleted or made unavailable.
- Temporary external Provider state is not authoritative StructaDoc state.
- Original uploads must be retained; a PDF produced by conversion is a separate Artifact
  and must not replace the original file.
- Submit the source file when the Provider supports the source format; otherwise convert
  it with the LibreOffice inside the image and submit that.
- Large files and raw parsing results go into local or S3-compatible storage; business
  metadata, structured fields, and storage references go into the database.
- Business persistence must support SQLite, PostgreSQL, MySQL, and MariaDB; the Domain,
  Application, and public API layers must not depend on a single database dialect.
- Workers must claim jobs atomically, maintain leases, and recover from crashes on every
  supported database. SQLite supports only one application instance; server databases
  support multiple Worker instances.

## Public contract and compatibility

- The public HTTP API uses versioned paths.
- A breaking change to public DTOs, status values, Block types, or coordinate semantics
  must bump the contract major version.
- Within the same major version, prefer adding optional fields.
- API clients must tolerate unknown fields and unknown Block types.
- Provider-native fields may appear only in extensions explicitly marked unstable or in
  Raw Artifacts.
- Internal database references and storage references must not appear in public API
  fields.

## Security constraints

- Never commit real tokens, passwords, connection strings, or private document samples.
- Provider tokens and storage credentials must not be returned to browsers or written to
  logs.
- Provider credentials in the database must be encrypted; the master key is injected
  through environment variables or deployment secrets.
- Online Providers transfer data to external parties; the UI and documentation must state
  this clearly.
- Upload validation must not trust client MIME types alone; file size, processing time,
  memory, and temporary disk usage must be bounded.
- External URLs, presigned URLs, and callbacks must defend against SSRF and use short
  validity periods and least privilege.

## Scope discipline

### During implementation

- Behavior changes and bug fixes must come with automated tests proportionate to the
  risk.

## Documentation impact

ADRs record the choice, the rationale, the constraints, and the consequences. Migration
implementation details belong in implementation issues; ADRs do not contain
Provider-specific SQL, numbered migration steps, or test checklists.

When any of the following changes, the authoritative documents must be updated in the
same change:

- the product boundary or explicit non-goals;
- the public API, state machines, or the Canonical Model;
- the meaning of the Provider interface or of capability declarations;
- data ownership, Artifact retention or deletion rules;
- authentication, credentials, external transfer, or security boundaries;
- deployment dependencies or key operational procedures.

Internal refactoring, formatting, local renames, and behavior-preserving test additions
usually need no new design document.

## Coding, dependencies, and databases

- Follow the repository's existing technology stack, directory structure, test tooling,
  and dependency management approach.
- Before adding a dependency, confirm that existing code and platform capabilities
  cannot provide an equivalent solution.
- Database changes use reviewable, repeatable migrations; ad-hoc startup DDL is not
  allowed.
- Every supported database must have independently executable migrations and run the
  same persistence and lifecycle contract tests.
- Provider-specific code must not leak into the Domain or public DTOs.

## Verification and safe operations

- Before deleting, moving, or bulk rewriting, confirm the exact targets.
- Before committing, check documentation links, formatting, secrets, and the repository
  state.

## English working environment

Write in English: project-maintained documentation (README and docs), Issue and Change Request titles and bodies, Comments, Discussions, Review Threads, and future commit messages, unless a maintainer explicitly requests another language. Preserve historical Issues, Change Requests, and comments. Do not translate code, commands, paths, identifiers, logs, technical data, or quoted external text. Preserve other project conventions.
