# Provider Result Normalization

This note describes the implemented Provider ZIP-to-Canonical Parse Bundle boundary. See [Provider Result Intake](./provider-result-intake.md), the [Canonical Document Model](../specifications/canonical-document-model.md), and [Canonical Result Persistence](./canonical-result-persistence.md).

## Normalization Contract

`IProviderResultNormalizer.Supports(providerType)` declares adapter coverage. A normalizer receives Parse Run ID, Provider type, validated `StoredProviderArchive`, and sanitized model/backend snapshots.

`MinerUResultNormalizer` supports `mineru-cloud` and `mineru-local` for observed layouts only. It does not expose MinerU fields as a public contract.

Before reading entries, it reopens the fixed archive and compares entry count, NFC-normalized path, directory flag, expanded size, and compressed size with the intake manifest. A non-seekable stream is copied to a delete-on-close temporary file under the verified archive size. Provider paths are never joined to a host filesystem path.

## Recognized MinerU Output

| ZIP content | Selection rule | Canonical result |
|---|---|---|
| `full.md` or one unique `*.md` | Exact root name first, otherwise one unique nested non-empty UTF-8 Markdown file | `markdown` Artifact |
| `content_list.json` | Exact root, unique `*_content_list.json`, then unique child name | `content-list` Artifact and Blocks |
| `content_list_v2.json` | Same priority, optional | Second `content-list` Artifact |
| `layout.json` / `*_middle.json` | Exact, unique suffix, or unique child name; optional | `layout` Artifact |
| `model.json` | Exact, unique suffix, or unique child name; optional | `model-output` Artifact |
| `images/**` or one nested `*/images/**` | Non-empty files under the validated manifest | Assets for PNG, JPEG, GIF, and WebP signatures |
| Original ZIP | Fixed intake object | `provider-archive` Artifact |

Multiple candidates at one priority are an explicit ambiguity error; archive order never chooses the “first.” JSON must be valid UTF-8, and the content-list root must be an array. A missing content list can still produce a valid Markdown-plus-archive bundle.

## Block Mapping

- Array order becomes a zero-based globally contiguous `sequence`.
- The zero-based MinerU `page_idx` becomes a one-based Page; the older `page_id` spelling is read only when `page_idx` is absent. When `page_idx` is present it alone decides the page, so an invalid `page_idx` rejects the bundle even if `page_id` is valid. Absent is `null`; a negative, non-integer, or `null` value rejects the bundle with `mineru-result-page-invalid`. The zero-based value is kept in the Page `sourceLocator` as `providerPageId`.
- Content comes from the first present, non-null field in this order: `text`, `content`, `table_body`, `code_body`, `list_items`, then the legacy `body`. `list_items` (an array of strings) is joined with `\n`. Other objects and arrays use compact JSON text.
- `contentFormat`: a `text_format` value wins (for example `latex`); a formula without one is `latex`; `table_body` or `body` content that starts with `<` after trimming is `html`; any other content is `plain`. StructaDoc does not sanitize table HTML; clients that render `html` content must sanitize it.
- Positive `text_level` maps to `title` with `heading-{level}` subtype. Otherwise a `sub_type` becomes the subtype, and unknown types use the normalized type token.
- A 0–1 bounding box remains unchanged; a 0–1000 box is divided by 1000; ambiguous coordinates produce no box.
- A 0–1 `score` becomes confidence.
- `img_path` resolves only against a validated archive-relative Asset alias.

MinerU `type` values map as follows (underscores are read as hyphens):

| MinerU `type` | Canonical type | Subtype |
|---|---|---|
| `text` with positive `text_level`, `title`, `heading` | `title` | `heading-{level}` when a level is given |
| `text`, `paragraph` | `text` | `sub_type` when present |
| `list`, `list_item` | `list` | `sub_type` (for example `text`, `ref_text`) |
| `table` | `table` | `sub_type` when present |
| `equation`, `formula`, `inline_equation`, `interline_equation` | `formula` | `sub_type` when present |
| `image`, `figure` | `image` | `sub_type` when present |
| `chart` | `image` | always `chart`, even when the item has its own `sub_type` |
| `code`, `algorithm` | `code` | `sub_type` (`code` or `algorithm`) |
| `header`, `page_header` | `header` | `sub_type` when present |
| `footer`, `page_footer` | `footer` | `sub_type` when present |
| `footnote`, `page_footnote` | `footnote` | `sub_type` when present |
| `page_number` | `page-number` | `sub_type` when present |
| any other value, such as `aside_text` or `index` | `unknown` | `sub_type` when present, otherwise the normalized type token, such as `aside-text` or `index` |

### Captions and Footnotes

The MinerU item `type` chooses which caption and footnote fields fill the Block's `caption` and `footnote` ([Canonical Document Model §8](../specifications/canonical-document-model.md#captions-and-footnotes)). Fields that belong to another type are not read and stay in the retained `content-list` Artifact.

| MinerU `type` | `caption` from | `footnote` from |
|---|---|---|
| `image`, `figure` | `image_caption`; the legacy `img_caption` only when `image_caption` is absent | `image_footnote`; the legacy `img_footnote` only when `image_footnote` is absent |
| `chart` | `chart_caption` | `chart_footnote` |
| `table` | `table_caption` | `table_footnote` |
| `code`, `algorithm` | `code_caption` | `code_footnote` |
| any other type | not read | not read |

As with `page_idx` and `page_id`, a current field name that is present, even as an empty array, alone decides; the legacy name is then not read.

MinerU writes each field as an array of inline text strings. The value becomes:

- an array: its string entries in array order, skipping non-string entries and entries that are empty or whitespace only, joined with `\n`; null when nothing is left. Entries are not trimmed or reformatted;
- a string: that string, or null when it is empty or whitespace only;
- absent, `null`, a number, a boolean, or an object: null.

A malformed value never fails normalization and is not logged. An oversized caption or footnote fails Parse Bundle validation with the same size codes as oversized content, which the normalizer reports as `mineru-result-bundle-invalid`.

### Field Coverage

The normalizer declares the content-list fields it reads in `MinerUResultNormalizer.RecognizedContentListFields`. One MinerU field is deliberately not read and stays available in the retained `content-list` Artifact: `anchor`, an internal cross-reference target with no canonical field.

The test fixture `tests/StructaDoc.Persistence.Tests/Fixtures/MinerU/pipeline_content_list.json` follows the field structure of real MinerU `pipeline` output with synthetic text. A test fails when the fixture contains a field that is neither recognized nor on the ignore list, so a fixture refresh cannot drop a new or renamed field silently.

MinerU writes a page on every item. When a content list has items and more than half of them carry neither `page_idx` nor `page_id`, the normalizer logs one warning for that normalization. The warning carries only the Parse Run (or segment) ID, the Provider type, the item count, and the number of items without a page field; it never logs content, file paths, or field values.

Raw JSON is already retained as an Artifact, so Blocks do not copy complete Provider objects into `providerData`. This prevents internal paths and future unknown sensitive fields from entering ordinary public responses.

## Idempotent Storage and Identity

Derived resources use stable keys:

- `parse-runs/{parseRunId}/artifacts/*.json|*.md`;
- `parse-runs/{parseRunId}/assets/{entryPathHash}.{extension}`.

Equal content reuses a key; different content conflicts without overwrite. Block, Asset, and Artifact UUIDs derive deterministically from Parse Run ID and stable logical origin. A crash after object writes but before bundle commit therefore recreates the same fingerprint on recovery.

## Configuration

| Key | Default | Meaning |
|---|---:|---|
| `ProviderResultNormalization:MaxMarkdownBytes` | 64 MiB | Per-Markdown read/write limit |
| `ProviderResultNormalization:MaxJsonBytes` | 64 MiB | Per-JSON read/write limit |
| `ProviderResultNormalization:MaxAssetBytes` | 256 MiB | Per-Asset streaming limit |
| `ProviderResultNormalization:TemporaryPath` | OS temp `structadoc-provider-normalization` | Fallback for non-seekable archives |

Archive limits and aggregate Parse Bundle limits apply in addition.

Printed page labels are not Provider output and are not read here: one Provider-neutral step adds them after normalization, partly from the `page-number` Blocks this normalizer produces. See [Canonical Result Persistence](./canonical-result-persistence.md#page-labels).

## Remaining Work

- more observed MinerU layout versions, image media types, and structured fields;
- versioned normalization adapters if an upstream change cannot be handled additively.
