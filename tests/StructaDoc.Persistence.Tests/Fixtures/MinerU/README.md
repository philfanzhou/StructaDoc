# MinerU Content List Fixture

`pipeline_content_list.json` follows the field structure of a real MinerU `pipeline` content list: field names, nesting, and value types. Every text value, image name, and number in it is synthetic; no third-party or private document content is included.

The structure was checked against MinerU's own Content List V1 renderer, `mineru/render/_internal/content_list/v1.py` in `opendatalab/MinerU` at commit `b7f3dd0` (release 4.0.12), plus the item types observed in a real MinerU Cloud `pipeline` run. Each item has a zero-based `page_idx` and an integer `bbox` on a 0–1000 scale.

`MinerUResultNormalizerTests` checks that every field in this fixture is either read by the normalizer (`MinerUResultNormalizer.RecognizedContentListFields`) or listed in the test's ignore list. When refreshing the fixture from newer MinerU output, a new or renamed field fails that test until it is mapped or deliberately ignored.

## MinerU 4 archive fixtures

`mineru-4.0.12-html.zip` is the unmodified ZIP returned by an actual pinned MinerU
**4.0.12** server (`create_app`, `flash` tier), using docvortex **0.5.16**, on
2026-10-19. `mineru-4.0.12-source.html` is its entire synthetic input. It required
no model download, Cloud credential, or private document. The ZIP contains
`markdown.md`, `structured_content.json`, `middle_json.json`, and
`model_output.json`. Its six Blocks are two titles, text, a list, a Markdown table,
and fenced code. HTML has `page_count_kind: logical`, so these Blocks have source
locators but no physical Pages. The test reads the ZIP through ordinary bounded
intake without Python or MinerU installed.

`mineru-4-physical-structured-content.json` is hand-authored synthetic data using
that shipped renderer's `pages[].blocks[]` shape. It covers nonzero physical
indexes, an empty declared page, every mapping family, 0–1 boxes, image references,
and annotation objects. It is not a PDF inference result. The two intentionally
synthetic raw-only metadata values verify that metadata and extensions never enter
ordinary Blocks or Provider metadata. Tests supply signature-valid synthetic image
bytes separately, as in the legacy fixture.

The actual renderer was inspected at
`docvortex/render/_internal/structured_content/renderer.py` in the pinned installed
package. No MinerU or docvortex implementation code is included. The observed
structure differs from upstream's draft Structured Content schema: it has no
`mineru.structured_content` envelope, and Block `content` is already a string.
The [normalization note](../../../../docs/development/provider-result-normalization.md#mineru-4-structured-content)
records the supported mapping and limits; arbitrary future schemas and physical
pages inferred from logical HTML/Office pages are not supported.

`pipeline-legacy-baseline.zip` packages the existing synthetic Content List fixture,
Markdown, empty legacy JSON Artifacts, and signature-valid synthetic JPEG bytes.
Its entry timestamp is fixed at 2026-01-01. With Parse Run
`d54cc20e-d1de-46de-8978-8377590bbc5a` and model `pipeline`, the unchanged mainline
normalizer at `05b6aa5` produced fingerprint
`1e15d27b17be8edec0d6887616abc514b6da75209845213e950db0afd7cbba05`.
The regression test reads the fixed ZIP bytes, so compression/platform differences
cannot change the assertion.
