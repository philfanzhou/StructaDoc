# ADR-0013: Adopt a Non-Archiving Image Layout-Detection Processing Mode

- Status: Accepted (points 2 and 4 superseded in part by [ADR-0014](./0014-cloud-layout-backend.md))
- Date: 2026-10-24
- Related issue: [#190](https://github.com/philfanzhou/StructaDoc/issues/190)

## Context

The consumer Ruoyu.Study needs page-region candidates — normalized rectangles marking
where text, figures, tables, and formulas sit on a page image — before its own
multimodal models assemble questions from them. Its inputs are single JPEG or PNG page
images, and it needs the answer within one HTTP request/response exchange.

StructaDoc's only processing mode today is the retained-document lifecycle of
[ADR-0001](0001-product-boundary.md): ingest and retain an original, create a durable
Parse Run through a Provider ([ADR-0002](0002-parser-provider-abstraction.md)),
normalize and persist the result as Documents, Pages, Blocks, Assets, and Artifacts,
retain raw results for traceability, and expose everything through versioned APIs with
durable cleanup ([ADR-0007](0007-resource-lifecycle-and-cleanup.md)). Routing a
layout-only request through that pipeline would create a Document and a Parse Run per
request — a second document lifecycle the caller does not want and StructaDoc would
have to retain and clean up.

Facts verified against the implementation (mainline `4a0e6f9`, 2026-10-24):

- Standalone JPEG/PNG images never enter document ingestion: upload validation detects
  PDF, legacy Office, and OpenXML containers only. The new mode is genuinely new
  surface, not a reuse of the upload path.
- `ProviderCapabilities` expresses media types, file and page limits, and cancellation
  only; there is no layout-only capability to declare today.
- The MinerU Cloud and MinerU Local adapters implement full document parsing protocols
  only.
- Every current Provider result is bound to a persistent Parse Run and stored — exactly
  the lifecycle this mode must not create.
- The official MinerU SDK separates its layout path (`layout_detect()` /
  `aio_layout_detect()`) from `two_step_extract()` full parsing, so a genuine
  layout-only Provider path exists upstream.

## Decision

StructaDoc exposes an authenticated, non-persistent image layout detection API as one
explicit, enumerated exception to the retained-document lifecycle
([ADR-0001](0001-product-boundary.md)). The existing persistent document parsing,
result APIs, retention, and cleanup behavior are unchanged.

1. **Non-archiving processing.** A layout request creates no Document, Parse Run, Page,
   Block, Asset, Artifact, or request-result record, and stores no request image,
   result, or raw Provider response permanently. StructaDoc keeps request data only in
   bounded request-scoped memory and temporary files, cleaned on success, failure,
   timeout, and cancellation; crash leftovers are removed by startup and expiry
   cleanup. Because the mode creates no resources, the durable deletion machinery of
   [ADR-0007](0007-resource-lifecycle-and-cleanup.md) applies to it only as that
   safety net for temporary files, not as a per-request lifecycle.

2. **Layout-only execution through an explicit capability.** The mode runs layout
   inference only, through a distinct execution contract and an explicit capability
   declaration within the Provider abstraction of
   [ADR-0002](0002-parser-provider-abstraction.md) — a capability separate from full
   document parsing. A backend without the capability fails the request explicitly and
   never falls back to full document parsing. Superseded in part by
   [ADR-0014](0014-cloud-layout-backend.md): an explicitly selected cloud backend built on
   the MinerU Cloud extraction API is now allowed, because that service executes its full
   pipeline regardless of the submission switches and the maintainer confirmed cloud-side
   recognition is acceptable; the prohibition this point stated is narrower than the
   requirement needs.

3. **Provider-neutral, versioned response.** The public response is Provider-neutral
   and versioned under the public API's normal compatibility rules. Region geometry
   uses the canonical normalized bounding-box convention of the
   [Canonical Model](../specifications/canonical-document-model.md#9-bounding-box)
   (Section 9): top-left origin, `x` increasing rightward, `y` increasing downward,
   values in `[0, 1]`. Region IDs in the response are response-local references valid
   for that one reply, not Canonical Model resource IDs. The response carries no
   recognized body text, table HTML, formula LaTeX, cropped assets, Markdown, or
   permanent download URLs.

4. **Optional local inference runtime.** StructaDoc maintains an optional local
   inference runtime built on the official MinerU SDK layout path (`layout_detect()` /
   `aio_layout_detect()`), configured by administrators like other Providers, with the
   model loaded once and reused across requests and with bounded request concurrency.
   Its request-data retention matches the non-archiving flow of point 1. Superseded in
   part by [ADR-0014](0014-cloud-layout-backend.md): it is now one of two selectable
   backends (`Layout:Backend = layout-runtime`, still the default) rather than the only
   one; the runtime itself and its behavior are unchanged.

5. **Security boundaries.** A new explicit API-client permission guards the endpoint,
   separate from the existing document and parse scopes. Runtime endpoints, model
   locations, and credentials stay administrator-controlled within the settings and
   secret foundation of [ADR-0011](0011-settings-and-secret-foundation.md). Logs carry
   request identifiers, timing, counts, and status only — never image bytes, body
   content, raw model responses, credentials, or signed URLs.

6. **Caller responsibilities.** The caller retains original images and accepted
   results, schedules and retries requests, and maps results to its own versions.
   StructaDoc provides no durable replay and no exactly-once inference guarantee.

This ADR records a decision only, not delivery status. The implementation tasks it
depends on are [#193](https://github.com/philfanzhou/StructaDoc/issues/193) (the execution
contract and adapter), [#194](https://github.com/philfanzhou/StructaDoc/issues/194) (the public
endpoint), and [#195](https://github.com/philfanzhou/StructaDoc/issues/195) (the deployable
inference runtime); see the tracker and those issues for what exists on the mainline today.
[ADR-0014](0014-cloud-layout-backend.md) records the later correction that added the configured
MinerU Cloud service as a second, explicitly selected backend.

## Consequences

### Positive

- The caller receives page-region candidates without creating a second document
  lifecycle per request.
- The model is loaded once per deployment and reused across requests instead of being
  paid per request.
- The boundary exception stays explicit and auditable: one accepted ADR names the
  single non-retained mode, its limits, and its change rule.

### Trade-offs

- One more optional deployment component to configure, monitor, and keep current.
- No durable history exists for these requests; a caller cannot replay a past request
  through StructaDoc.
- In-flight accelerator work cannot always be interrupted immediately, so cancellation
  is cooperative and bounded rather than instantaneous.

## Change Rule

Extending this mode to content recognition, persistent image ingestion, or any archive
of requests requires a new ADR that changes this decision.
