# ADR-0002: Adapt Parsing Services and Normalize Their Results

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc initially supports both the hosted MinerU service and a self-hosted `mineru-api`. Their authentication, file transfer, submission, polling, result download, supported formats, limits, and lifecycle differ and may change independently.

If the public API, database, or Worker directly depended on one MinerU response format, consumers would inherit Provider details and switching between hosted and local services would become a breaking change.

## Decision

### 1. Provider adapters

Each parsing service integrates through its own adapter. A Provider can:

- validate administrator configuration and test connectivity;
- report supported formats, models, options, limits, and cancellation capability;
- submit a document;
- poll external status and progress;
- retrieve final results and raw artifacts;
- attempt cancellation when supported upstream;
- classify errors as transient, configuration, input, permanent, or security failures.

The initial adapters are MinerU Cloud and MinerU Local.

### 2. StructaDoc state is authoritative

The StructaDoc Parse Run is the authoritative job record. External Provider status is only an integration fact.

- Store the external task ID separately from the Parse Run ID.
- Short Provider retention or Provider restarts do not remove stored StructaDoc results.
- After a restart, resume an existing external task or saved result instead of submitting the document again.

### 3. Canonical results

Every Provider output maps to the [Canonical Document Model](../specifications/canonical-document-model.md).

- The public API promises only StructaDoc field semantics.
- Raw ZIP, JSON, and unknown fields remain authorized Raw Artifacts or explicitly unstable extensions.
- Provider-specific fields never become mandatory for every consumer.

### 4. Administrator-controlled configuration

- Regular users and API clients cannot modify Provider configuration.
- Provider tokens are never returned to browsers or written to logs.
- Every Parse Run snapshots the Provider type, configuration ID and version, and parsing options.
- Configuration versions are immutable; updates create new versions.
- Disabled versions cannot create new jobs, but remain decryptable while referenced by non-final Parse Runs.
- Changing the default Provider never changes existing Parse Runs.

### 5. Capability-driven file handling

1. Submit the original when the Provider supports its format.
2. Otherwise, convert supported Office formats to PDF with the built-in LibreOffice adapter when the Provider accepts PDF. See [ADR-0003](./0003-technology-and-single-image-deployment.md).
3. Retain the original and store the converted PDF as a separate Artifact.
4. Record source format, submitted format, and conversion facts on the Parse Run.

## Consequences

### Positive

- Hosted and local MinerU can be exchanged without changing the public API.
- MinerU protocol changes remain within their adapters.
- Future parsers can be added without polluting Domain or public contracts.
- Retained raw artifacts make normalization defects traceable and reprocessable.

### Trade-offs

- Provider capability models and normalization fixtures require maintenance.
- Different Providers cannot guarantee identical results; the canonical model permits absent fields and unknown types.
- Raw and normalized representations consume additional storage.
- Cancellation is best-effort and depends on Provider support.

## Rejected Alternatives

### Expose MinerU responses directly

Rejected because it couples consumers to MinerU versions, schemas, and deployment modes.

### One hard-coded client for Cloud and Local

Rejected because the protocols and lifecycles differ; conditional branches would obscure error and recovery semantics.

### Convert every Office file to PDF first

Rejected as the default because native submission may preserve more semantics. Conversion is a fallback only when needed.
