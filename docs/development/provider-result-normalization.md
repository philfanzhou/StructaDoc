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
- Observed zero-based MinerU `page_id` becomes a one-based Page; absent is `null`; invalid/negative values reject the bundle.
- `text`, `content`, then `body` supplies content. Objects/arrays use compact JSON text.
- Known text, table, equation/formula, image/figure, code, header/footer values map to registered canonical types; unknown values map to `unknown` with a safe subtype when possible.
- Positive `text_level` maps to `title` with `heading-{level}` subtype.
- Text format, formula, and HTML table bodies determine `contentFormat`.
- A 0–1 bounding box remains unchanged; a 0–1000 box is divided by 1000; ambiguous coordinates produce no box.
- A 0–1 `score` becomes confidence.
- `img_path` resolves only against a validated archive-relative Asset alias.

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

## Remaining Work

- more observed MinerU layout versions, image media types, and structured fields;
- broader production fixtures for tables, formulas, headings, and multipage images;
- versioned normalization adapters if an upstream change cannot be handled additively.
