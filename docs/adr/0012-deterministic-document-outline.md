# ADR-0012: Propose a Deterministic Parse Run Outline

- Status: Proposed
- Date: 2026-10-19
- Related issue: [#162](https://github.com/philfanzhou/StructaDoc/issues/162)

## Context

Callers currently receive ordered Blocks, including `title` Blocks with optional
`heading-N` subtypes, but no shared chapter/section hierarchy. Original PDFs can
also declare bookmarks that a Provider does not preserve. Rebuilding this
information in every caller loses evidence available only with the source file.

[ADR-0001](0001-product-boundary.md) already includes generic document structure
and location. A hierarchy copied from declared document structure stays inside
that boundary: it does not generate questions, lessons, business entities,
keywords, or other domain metadata. No product-boundary change is proposed.

This record proposes a later implementation. This change adds no runtime code,
DTO, migration, API route, Provider setting, or Canonical Model version. The
current [Canonical Model](../specifications/canonical-document-model.md) remains
the implemented contract. Acceptance of this ADR and a separate implementation
issue are required before that changes.

## Proposed decision

### Ownership and lifecycle

An outline belongs to one Parse Run and refers only to that run's immutable
canonical Blocks and Pages. Generate it after normalization, complete document
merging, and page-label enrichment, before publishing the run's result. A
segmented PDF must use global Block sequences and bookmarks from the full
submitted PDF, never segment-local positions. Re-parsing may create a different
outline; it cannot rewrite a prior run. Keep a bounded generator/version fact
with the result so its provenance is reproducible.

Use the submitted original PDF, or the separate `normalized-pdf` conversion
Artifact when conversion actually occurred. Never replace the original upload.
Native Word results do not automatically have PDF bookmarks or physical Pages;
use only their available canonical evidence, with nullable physical location.

### Sources and precedence

Prefer explicit document declarations over predictions. The proposal changes
the issue's initial ordering in light of the measurements below:

1. **PDF bookmarks:** retain declared titles and nesting, after resolving each
   destination to an unambiguous same-run Block anchor.
2. **Printed table of contents:** accept a literal entry only when its printed
   page reference uniquely matches `Page.label`, its title uniquely anchors to
   a Block there, and indentation or explicit numbering declares its level.
3. **Generic numbering:** use decimal sections (`1.2.3`), Chapter/Part prefixes,
   and explicitly supported localized forms to recover a level from an
   already recognized title candidate. A bare numbered prose/list line is not
   sufficient evidence to publish a node.
4. **Provider headings:** retain normalized `title` and `heading-N` evidence
   when stronger document declarations do not resolve the node. A missing
   level is insufficient to invent a parent or level.

Precedence resolves competing evidence for the same start Block; do not emit
one node per source for the same heading. Lower-priority evidence may fill
uncovered starts only when it fits the declared ordering, hierarchy, and ranges.
Contradictory or ambiguous entries are skipped. Do not flatten declared levels,
fill missing ancestors with invented nodes, or guess gaps in page labels.
A lower-priority level cannot override a bookmark or validated contents level.
Exact title/Block matching is the conservative baseline; broader matching
requires its own measured acceptance evidence in the implementation issue.

Provider heading evidence may originate in an existing parser's models,
including a VLM. Consuming that existing normalized structure does not authorize
an additional model call for outline inference. Outline generation itself is
entirely deterministic and must not invoke an LLM or another inference model.
Exclude semantic chapter naming, domain-specific units/activities, generated
summaries, and manual editing. Caller corrections remain caller-owned state.

### Node and API shape

The later Canonical Model should add an outline collection with these semantics:

| Field | Proposed meaning |
|---|---|
| `id` | Opaque same-run node identity, deterministic for the committed result |
| `parentId` | Nearest preceding accepted ancestor; null for a root |
| `level` | Positive declared hierarchy level; preserve skipped levels without creating nodes |
| `title` | Source title as untrusted text, with bounded length |
| `startBlockSequence` | Exact global canonical start Block, inclusive |
| `endBlockSequence` | Inclusive last Block before the next accepted node at this or a shallower level, or the run's final Block |
| `startPageNumber` | Canonical one-based physical Page, or null where no reliable physical Page exists |
| `source` | `pdf-bookmarks`, `printed-toc`, `generic-numbering`, or `provider-headings` |
| `confidence` | Categorical evidence strength: `high` for declared bookmarks, `medium` for validated contents, `low` for numbering/provider evidence; not a probability or the Block's numeric confidence |

Every emitted node needs a valid start sequence. Parent ranges contain children;
end precedes no start; all references stay within one Parse Run. A bookmark that
only points at a page without a unique matching Block is insufficient. The
implementation must bound node count/depth/title size and validate nesting and
range consistency before publication. It may not expose Provider IDs, paths,
storage references, credentials, or external task state.

Propose a read-only `GET /api/v1/parse-runs/{parseRunId}/outline` with nodes in
start sequence order and an explicit availability reason distinguishing generated,
no usable structure, generation unavailable, and older runs. Reuse existing
Parse Run result authentication, `parses:read` scope, and resource authorization;
being able to name an ID does not grant access. There is no editing endpoint.
Exact DTO/error/cache details belong to the later contract change.

### Failure, compatibility, and rollback

No usable evidence produces an empty outline. A bounded extraction or outline
validation failure records a sanitized diagnostic and an empty/unavailable
outline; it must not fail an otherwise valid Parse Run. Ordinary cancellation,
lease ownership, canonical persistence, and source-storage failures retain their
existing lifecycle guarantees: an optional outline must not bypass them or
publish a partial result. Persist only a complete validated outline alongside
the immutable result, never a partly built tree.

The later implementation is additive within `/api/v1` and a minor Canonical
Model revision. Existing Blocks, their sequence, Page numbering/labels, and
existing endpoints keep their meaning. Old Parse Runs remain readable and report
outline unavailable/empty without inferring new content during a read. Consumers
may ignore this optional resource and must tolerate added source/confidence
values. No schema/API compatibility change occurs in this documentation PR.

Rollback before release means withdrawing the proposal. After implementation,
stop generating outlines for new runs while preserving readable historical
results and the released read route. Do not delete committed outlines or alter
Blocks to roll back this optional feature. Any incompatible released node or
coordinate meaning requires the normal major-version process.

## Evaluation and rationale

The [evaluation directory](../../evaluations/document-outline/README.md) contains
five synthetic inputs, independent expected outlines, exact input hashes,
reproduction tools, raw synthetic Content Lists, normalized Blocks, per-source
measurements, and environment/model revisions. There are twelve expected nodes:
three each in the book, printed contents, technical report, and Word sample;
the unstructured sample expects none. Start means exact title at the authored
physical page, not a claimed bounding-box accuracy. Two headings share a page,
and actual Provider anchors retain their canonical Block sequence.

### Document declarations

| Source | Correct start and level on the documents declaring it | Across all 12 expected nodes | False positives |
|---|---:|---:|---:|
| PDF bookmarks: book and converted Word | 6/6 | 6/12 | 0 |
| Printed contents with unique page labels | 3/3 | 3/12 | 0 |
| Generic decimal numbering: report | 3/3 | 3/12 | 0 |

These are independent PDF-text/destination experiments, not a claim that every
Provider can anchor those declarations. [Full per-file results](../../evaluations/document-outline/results/deterministic.json)
retain missing nodes when a source is absent. Unit tests reject absent/repeated
page-label matches, title mismatches at the destination, incorrect pages/levels,
and duplicate detections. They also show the limited prefix grammar, not broad
multilingual document quality. Simple numbering cannot distinguish every list
from a heading, which is why this proposal requires an existing title candidate.

### Word conversion

The production image's LibreOffice **24.2.7.2** converted the native DOCX with the
unchanged production arguments and a unique profile. All **3/3** heading-style
nodes survived as PDF bookmarks at levels **1, 2, 1**, on pages **1, 1, 2**.
The original DOCX remained separate. A non-fatal `javaldx` warning was observed;
conversion exited 0. [Version, image ID, command, and outcome](../../evaluations/document-outline/results/libreoffice.json)
identify this actual run. This supports using converted bookmarks, but does not
prove fidelity for every font, template, or LibreOffice version. Native DOCX
parsing was not measured: both backends received this same converted PDF.

### Local MinerU 3.4.5 comparison

All ten CLI commands and all ten production-normalizer invocations completed
successfully. Both backends received the identical five PDFs, including the
LibreOffice-produced Word PDF, on an Apple M4 with 16 GiB memory. Pipeline used
MPS; `vlm-engine` selected **mlx-engine** (mlx-vlm 0.3.9, MLX 0.31.1). Both used
MinerU **3.4.5**. These are local measurements, not MinerU Cloud or 4.x quality tiers.

| Input | Expected | Pipeline correct start | Pipeline correct level and start | VLM correct start | VLM correct level and start | Extra titles, each backend |
|---|---:|---:|---:|---:|---:|---:|
| Book | 3 | 3 | 1 | 3 | 2 | 0 |
| Printed contents | 3 | 3 | 1 | 3 | 2 | 1 |
| Numbered report | 3 | 3 | 1 | 3 | 2 | 0 |
| Converted Word | 3 | 3 | 1 | 3 | 2 | 0 |
| Unstructured prose | 0 | 0 | 0 | 0 | 0 | 0 |
| Total | 12 | 12 | 4 | 12 | 8 | 1 |

Pipeline assigned level 2 to every detected heading; VLM assigned level 1.
Neither reconstructed the authored 1/2 hierarchy. Both also recognized the
contents container title, `Contents`, which is outside the independently authored
section outline and counts as a false positive. There were no missing authored
titles. [Full Provider scores](../../evaluations/document-outline/results/provider.json)
keep title matches, correct starts, correct levels, joint results, misses, extra
titles, and each measured Block sequence separately.

All **24/24** declared-source nodes (12 per backend: bookmarks 6, contents 3,
numbering 3) uniquely matched actual canonical Blocks, with **0** unresolved
anchors. [Source anchor results](../../evaluations/document-outline/results/source-anchors.json)
record those positions. For example, the printed-contents entries resolve to
pipeline sequences 2/6/10 and VLM sequences 4/8/12; both map to pages 2/2/3 with
levels 1/2/1 supplied by the contents declaration. The Word bookmarks resolve to
sequences 0/4/8 in both outputs, preserving the declared levels despite the
Provider's flattened levels. Unstructured prose travels through both parsers
and normalization with no heading nodes. These are complete fixture-to-parser-
to-canonical-anchor walkthroughs, not just document-format checks.

The exact downloaded model revisions are recorded in
[environment.json](../../evaluations/document-outline/results/environment.json):
`PDF-Extract-Kit-1.0` at `ed6b654c018d742e65a17671e379c5e6ecc87ec9`
(1,082,446,509 bytes of selected files), and `MinerU2.5-Pro-2605-1.2B` at
`08aaea840498d49ce16247b6263196cf02814885` (2,328,028,751 bytes).
Reproduction fetches those revisions and selected paths explicitly, rather than
assuming a model repository's latest branch is unchanged. Weights and large
intermediate files stay outside Git.

[Execution records](../../evaluations/document-outline/results/execution.json)
contain every command, exit status, timeout, and elapsed time. Cold runs took
73.877 seconds for pipeline and 81.237 seconds for VLM, including downloads and
startup. The VLM printed-contents record includes a resource scheduling pause of
the batch coordinator after its parser subprocess completed; its 315.811-second
wall time is explicitly annotated and cannot be used as an inference-speed
comparison. No parser was interrupted, failed, or timed out. This experiment
compares heading quality, not benchmark throughput.

### Consequences and limits

Declared bookmarks and validated contents preserve the authored hierarchy in
this small corpus; title detection alone is weaker evidence of hierarchy. Keep
both source and confidence visible so a caller can distinguish declarations from
lower-confidence fallbacks. Prefer conservative omission to an invented tree.

This is an easy, English, text-based sanity corpus rather than a general quality
benchmark. It does not measure scanned documents, complex layouts, duplicate
headings, end-of-section accuracy, real-world numbering false positives, or
adversarial PDFs. No combined production generator was built or scored here: source selection,
contents-container suppression, combining hierarchy evidence, and section end ranges remain
acceptance work for the later implementation. The later implementation needs its own bounds, cancellation,
authentication, concurrency, and persistence acceptance tests. Those runtime
outcomes are not applicable to this documentation/evaluation change. No default
MinerU model is changed, and no outline feature or workspace UI is implemented.
