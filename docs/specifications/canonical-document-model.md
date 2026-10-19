# Canonical Document Model

- Status: Implemented specification
- Version: 1.1
- Last updated: 2026-10-19
- Pending transition: ADR-0009 actor persistence is target behavior until #35 is
  implemented

## 1. Purpose

This specification defines the Provider-neutral structured document semantics shared by StructaDoc Providers, persistence, the web workspace, and the public API.

MinerU Cloud, MinerU Local, and future Provider outputs map to this model before consumers receive them. Raw Provider artifacts may be retained for traceability, but they never replace the canonical model or become a stable public contract.

The current implementation persists and reads Documents, Parse Runs, Pages, Blocks, Assets, and Artifacts; validates and commits Parse Bundles idempotently; normalizes observed MinerU layouts; and exposes authorized result, content, Markdown, and export routes.

## 2. Design Principles

1. **Provider-neutral:** fields do not depend on a MinerU API version.
2. **Traceable:** canonical content links to its Parse Run, original, and retained artifacts.
3. **Loss-aware:** information that cannot be normalized remains in a Raw Artifact rather than disappearing silently.
4. **Extensible:** consumers tolerate unknown Block types and additive optional fields.
5. **Stable ordering:** every Block has one global reading sequence even when reliable physical pages do not exist.
6. **No domain inference:** the model describes document structure, not questions, knowledge points, invoice entities, or other domain output.

### 2.1 Internal and Public Fields

The model covers normalization, persistence, and API semantics, but not every internal field is serializable:

- `storageRef` is internal and never appears in public DTOs;
- public access uses StructaDoc resource IDs and authorized content endpoints;
- `providerData` is an explicitly unstable, bounded, sanitized extension and is absent from ordinary list DTOs;
- Provider tokens, presigned URL queries, internal paths, checkpoints, external task IDs, and database keys are never public;
- API DTOs are explicit projections and do not serialize persistence entities directly.

## 3. Relationships

```mermaid
erDiagram
    DOCUMENT ||--o{ PARSE_RUN : has
    PARSE_RUN ||--o{ PAGE : describes
    PARSE_RUN ||--o{ BLOCK : contains
    PARSE_RUN ||--o{ ASSET : extracts
    PARSE_RUN ||--o{ ARTIFACT : produces
    PAGE ||--o{ BLOCK : locates
    BLOCK o|--o| ASSET : references
```

A Document may be parsed repeatedly with different Providers, models, or options. Each Parse Run owns its own Pages, Blocks, Assets, and Artifacts and never overwrites historical results.

## 4. Identifiers and Time

- StructaDoc resources use opaque UUIDs; database-generated integers and Provider IDs are not public identifiers.
- An external Provider task ID is Parse Run integration metadata only.
- Time uses UTC semantics and is emitted as ISO 8601 with an offset.
- Storage paths are not resource IDs and consumers must not depend on their shape.

## 5. Document

A Document represents the immutable original managed by StructaDoc.

| Field | Required | Semantics |
|---|---|---|
| `id` | Yes | StructaDoc Document ID |
| `originalFileName` | Yes | Sanitized display name; never a storage path |
| `mediaType` | Yes | Server-detected media type |
| `extension` | Yes | Normalized lowercase extension |
| `sizeBytes` | Yes | Original bytes |
| `sha256` | Yes | Lowercase original-content hash |
| `storageRef` | Internal | Storage reference excluded from public APIs |
| `createdBy` | No | Internal actor audit fact; target persistence under [ADR-0009](../adr/0009-canonical-persisted-actor-identity.md) uses a canonical `(issuer, subject)` pair for new rows and an exact opaque payload for migrated rows |
| `owner` | No | External owner identified by `(issuer, subject)` |
| `createdAt` | Yes | Creation time |

### Invariants

- Conversion output never overwrites the original.
- A user filename never participates in a server path.
- Client-declared MIME type is not authoritative.
- A Document has no caller-defined business metadata; consumers keep their own keys against its
  stable ID unless a future version defines an explicit contract for them.
- Physical content deduplication, if introduced, does not merge distinct Document resources.
- Deletion-pending Documents are unavailable to ordinary reads.
- `createdBy` is not exposed by the current `/api/v1` `DocumentResponse`; its
  canonical pair and legacy compatibility payload are persistence details, not
  public storage fields.

## 6. Parse Run

A Parse Run is one immutable parsing intent and its execution outcome.

| Field | Required | Semantics |
|---|---|---|
| `id` | Yes | StructaDoc Parse Run ID |
| `documentId` | Yes | Target Document |
| `status` | Yes | Stable value from the Parse Job Lifecycle |
| `stage` | No | Diagnostic progress, not final-state authority |
| `providerType` | Yes | For example `mineru-cloud` or `mineru-local` |
| `providerConfigId` | Yes | Logical administrator configuration |
| `providerConfigVersion` | Yes | Immutable configuration version snapshot |
| `options` | Yes | Sanitized OCR/table/formula/language and parsing options |
| `sourceMediaType` | Yes | Original media type |
| `submittedMediaType` | Yes | Media type actually submitted to the Provider |
| `conversion` | No | Converter snapshot and converted Artifact ID |
| `externalTaskId` | Internal | Provider task ID; excluded from public APIs |
| `attemptCount` | Yes | Started execution attempts |
| `errorCode` | No | Stable machine-readable StructaDoc code |
| `errorMessage` | No | Sanitized human-readable diagnosis |
| `createdAt` | Yes | Creation time |
| `startedAt` | No | First execution start |
| `completedAt` | No | Final-state time |

Credentials never enter `options` or public responses.

When present, `conversion` includes:

- `converterType`, currently `libreoffice`;
- actual `converterVersion`;
- `sourceMediaType` and `outputMediaType`;
- the same-Parse-Run `normalized-pdf` Artifact ID;
- bounded non-sensitive parameters without temporary directories, command paths, or host details.

This snapshot explains and resumes conversion; it never permits caller-supplied commands.

## 7. Page and Source Location

| Field | Required | Semantics |
|---|---|---|
| `number` | Yes | One-based StructaDoc page number |
| `width` | No | Provider-reported width |
| `height` | No | Provider-reported height |
| `unit` | No | Original dimension unit such as `point` or `pixel` |
| `sourceLocator` | No | Sheet, slide, section, or Provider-native page location |
| `label` | No | Printed page label, such as `iv` or `21` (added in 1.1) |
| `labelSource` | No | Where `label` came from: `pdf-page-labels` or `page-number-blocks` (added in 1.1) |

`number` stays the physical one-based page and remains the only page key Blocks use. `label` and `labelSource` are both present or both null.

Not every Office result has a reliable physical page. In that case:

- Block `pageNumber` is `null`;
- global Block `sequence` remains mandatory;
- sheet, slide, or section information belongs in structured `sourceLocator`;
- the normalizer does not invent a physical page.

### 7.1 Printed Page Labels

Printed books and reports often number pages differently from their physical order: front matter in roman numerals, the body restarting at 1. `label` carries the printed number so a caller can map a printed table of contents or a human page reference to a Page. It is display and lookup text, not a key: labels may repeat, and a Page may have none.

Labels are added by one Provider-neutral step after normalization, from the document actually submitted to the Provider (the original PDF, or the `normalized-pdf` Artifact for a converted Office file). For a large PDF split into segments, labels are read once from the full PDF, so the result matches the unsplit document. The Provider interface is not involved.

**Source 1, `pdf-page-labels`.** When the submitted document is a PDF with a valid ISO 32000 `/PageLabels` number tree that gives at least one Page a usable label, every Page gets the label of the range containing its zero-based physical index (`number - 1`), and source 2 is not used for any page.

- The tree is read through `/Nums` pairs and `/Kids`, at most 16 levels deep, with at most 10,000 visited nodes and ranges together, and with cycle detection. Each node has exactly one of `/Nums` or `/Kids`.
- Range start keys are non-negative integers, strictly increasing across the whole tree. Pages before the first range get no label.
- `/S` is `/D` (decimal), `/R` or `/r` (upper or lower roman), `/A` or `/a` (upper or lower letters: A to Z, then AA to ZZ, and so on), or absent (prefix only). `/St` is an integer of at least 1, defaulting to 1. `/P` is a text-string prefix.
- Any other type, a decreasing or repeated key, an unknown style, a cycle, or an exceeded limit makes the tree malformed. A malformed tree, or a PDF that cannot be parsed (corrupt, encrypted, unsupported), never fails the Parse Run: one warning without document content or paths is logged and source 2 is tried. A storage failure while reading the document keeps the Parse Run's ordinary transient failure and retry.
- Roman numerals are produced only up to 3999. A page whose formatted label is empty or only whitespace, longer than 256 characters, or contains a control character or an unpaired surrogate gets no label.

**Source 2, `page-number-blocks`.** Used only when source 1 gives no usable label: the submitted document is not a PDF, the PDF has no `/PageLabels`, or the tree is unusable.

- For each Page, the text of its `page-number` Blocks is trimmed of whitespace and of the decoration characters `-`, `–`, `—`, `.`, `(`, `)`, `[`, `]`. The rest must be 1 to 5 ASCII digits or a canonical roman numeral (any case) up to 3999; otherwise that Block is ignored.
- A Page whose accepted values disagree is skipped. Equal values count once, and the label is the trimmed text of the first such Block in reading order (for example `iv` or `21`), not a reformatted number.
- The accepted values must be consistent: every roman value comes before every arabic value in page order, and within each style `value - number` is the same for every accepted Page. At least two accepted values are required. Otherwise every label is null.

There is no interpolation: a Page without a label in the chosen source keeps a null label, even between two labeled Pages. The rules are deterministic, so the same inputs always give the same bundle fingerprint.

## 8. Block

A Block is the main structured content unit in the public API.

| Field | Required | Semantics |
|---|---|---|
| `id` | Yes | Block ID |
| `parseRunId` | Yes | Owning Parse Run |
| `sequence` | Yes | Zero-based contiguous reading order across the complete run |
| `pageNumber` | No | One-based StructaDoc page |
| `type` | Yes | Lowercase primary token |
| `subtype` | No | More specific Provider-neutral token |
| `content` | No | Primary text or structured string |
| `contentFormat` | No | `plain`, `markdown`, `html`, `latex`, or another registered format |
| `bbox` | No | Normalized page bounding box |
| `confidence` | No | Normalized 0–1 confidence |
| `assetId` | No | Referenced same-run Asset |
| `sourceLocator` | No | Sheet, slide, section, or similar source location |
| `providerData` | Internal/unstable | Bounded diagnostic extension, excluded from ordinary DTOs |

### Registered Block Types

- `title`
- `text`
- `list`
- `table`
- `formula`
- `image`
- `code`
- `header`
- `footer`
- `footnote`
- `page-number`
- `unknown`

`page-number` marks a printed page number on the page itself, as text content; it is a Block, not the Page's own `number`.

The set may grow within the same API major version. Consumers treat unknown types as displayable or ignorable Blocks rather than failing deserialization.

### Reading Order

- `sequence` is the only stable ordering across pages and types.
- Provider-local page order maps to one global sequence.
- Sequence is unique and contiguous within a Parse Run.
- A raw Provider ordering value may be retained internally but is never the public sort key.

## 9. Bounding Box

Public `bbox` uses Provider-neutral normalized coordinates:

```json
{
  "x0": 0.125,
  "y0": 0.240,
  "x1": 0.875,
  "y1": 0.420
}
```

- The origin is the top-left corner.
- `x` increases rightward and `y` downward.
- `0 <= x0 <= x1 <= 1` and `0 <= y0 <= y1 <= 1`.
- Emit a box only when it can be related to reliable page dimensions or a validated Provider-normalized coordinate system.
- Preserve original coordinates and conversion evidence in a Layout Raw Artifact or sanitized internal extension.
- Never manufacture apparently precise values from unverifiable heuristics.

## 10. Asset

An Asset is an extracted image or other binary resource.

| Field | Required | Semantics |
|---|---|---|
| `id` | Yes | Asset ID |
| `parseRunId` | Yes | Owning Parse Run |
| `name` | Yes | Normalized display name |
| `mediaType` | Yes | Server-confirmed media type |
| `sizeBytes` | Yes | Bytes |
| `sha256` | Yes | Content hash |
| `storageRef` | Internal | Storage reference excluded from public APIs |
| `width` / `height` | No | Pixel dimensions |
| `createdAt` | Yes | Creation time |

Public access uses an authorized content endpoint, never a permanent storage path.

## 11. Artifact

An Artifact is Parse Run output that is unsuitable for a Block or is retained as a larger file.

Registered types include:

- `normalized-pdf`
- `markdown`
- `provider-archive`
- `content-list`
- `layout`
- `model-output`
- `provider-raw`

Internally an Artifact contains at least `id`, `parseRunId`, `type`, `name`, `mediaType`, `sizeBytes`, `sha256`, `storageRef`, and `createdAt`, plus bounded type-specific metadata. A `normalized-pdf` records converter type/version and source media type.

One Parse Run may contain multiple Artifacts of one type; name or segment identity distinguishes them. Never represent a multi-part result by silently keeping only its first part.

## 12. Parse Bundle

A Parse Bundle is an internal normalization exchange object, not one database table:

```json
{
  "schemaVersion": "1.1",
  "parseRunId": "00000000-0000-0000-0000-000000000000",
  "pages": [],
  "blocks": [],
  "assets": [],
  "artifacts": [],
  "providerMetadata": {
    "providerType": "mineru-local",
    "model": "example-model"
  }
}
```

The complete bundle must validate and persist before the Parse Run becomes `succeeded`.

## 13. Versioning

- `schemaVersion` is `major.minor`.
- Additive optional fields and Block types increment minor semantics.
- Removed fields or changed field, coordinate, or ordering meaning requires a major version.
- API and Parse Bundle majors may evolve independently, but the API declares the semantics it returns.
- Raw Artifacts have no structural compatibility promise; record their Provider and version facts.
- A Parse Run keeps the `schemaVersion` it was committed with. New bundles use the current version; stored runs with an older minor version stay readable through every result endpoint, and no read path checks the stored version.

### Version History

| Version | Changes |
|---|---|
| 1.0 | Initial model |
| 1.1 | Optional Page `label` and `labelSource` (section 7.1); the registered `page-number` Block type. Runs committed under 1.0 keep that version and have null labels; re-parse a document to get labels |

## 14. Validation Requirements

At minimum, a normalized result satisfies:

- referenced Document and Parse Run exist;
- Block sequence is unique, contiguous, and in reading order;
- `pageNumber` is `null` or at least 1;
- a Page `label` and `labelSource` are both null or both set; a label is non-empty printable text of at most 256 characters, and `labelSource` is a registered value;
- every bounding box and confidence value satisfies its normalized range;
- `assetId` references an Asset in the same Parse Run;
- a conversion Artifact ID references a same-run `normalized-pdf`;
- Asset and Artifact hash and size match actual stored objects;
- extensions do not contain credentials, signed URL queries, internal paths, or other sensitive values;
- the Parse Run is marked successful only in the same transaction that accepts the full canonical result.

## 15. Evolution Work

Future additive work may define richer Artifact metadata keys, more Office sheet/slide/section locators, more Provider fixture versions, and additional registered Block types. Any incompatible public meaning follows the versioning rules above rather than leaking a Provider schema into the contract.
