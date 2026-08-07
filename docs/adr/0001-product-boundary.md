# ADR-0001: Limit StructaDoc to Document Ingestion and Structured Parsing

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc lets users upload PDF, Word, Excel, PowerPoint, and other supported documents, convert them into stable structured data through online or local parsing services, and expose that data through an API.

Parsed documents can later feed full-text search, vectorization, knowledge bases, RAG, question generation, or other domain workflows. If StructaDoc owned all of those concerns, it would become a parsing platform, search platform, and domain application at once. Its public contract would then be driven by individual consumers.

## Decision

StructaDoc is responsible for:

1. receiving and managing original documents;
2. creating and executing persistent asynchronous parsing jobs;
3. adapting external and local document parsing Providers;
4. normalizing Provider results into stable Documents, Parse Runs, Pages, Blocks, Assets, and Artifacts;
5. retaining originals, structured results, raw parsing artifacts, and parsing history;
6. exposing these resources through a versioned HTTP API and user-facing workspace.

StructaDoc is not responsible for:

- full-text or vector search, embeddings, or RAG pipelines;
- generating domain entities for question banks, vocabulary, contracts, invoices, or similar applications;
- online Office document editing;
- allowing consumers to access its database or object storage directly.

Consumers may search, vectorize, extract domain data, or otherwise post-process StructaDoc output within their own boundaries.

## Consequences

### Positive

- The public contract remains centered on document structure and independent of consumer technology.
- The default self-hosted deployment has fewer components.
- Different applications can process one parse result in different ways.
- Providers, storage, and APIs can evolve without coupling to a domain model.

### Trade-offs

- StructaDoc does not provide an upload-to-search knowledge-base experience by itself.
- Consumers must select their own search, vectorization, and domain-processing solutions.
- The public API must provide enough structure, location information, and authorized artifacts that consumers do not need to bypass it.

## Change Rule

Adding search, vectorization, RAG, or domain-specific entities to the StructaDoc core requires a new ADR that explicitly changes this decision.
