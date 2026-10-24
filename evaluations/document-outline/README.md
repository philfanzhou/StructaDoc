# Document outline evaluation

This is an evaluation for [ADR-0012](../../docs/adr/0012-deterministic-document-outline.md) (withdrawn before acceptance),
not a production outline feature. See [provenance](PROVENANCE.md), independent
[expected outlines](expected.json), and machine-readable [results](results/).

## Reproduce

The measured host is Apple Silicon macOS with 16 GiB memory. Use Python 3.12
and an isolated virtual environment on that platform to reproduce the MLX engine.
The full lock includes macOS-only MLX packages; it is not a portable Linux lock. These are evaluation-only
dependencies; none enters the StructaDoc runtime image. The checked-in lock lists
the actual installed versions. Model downloads and outputs must stay outside Git.
On the measured host, only one conversion/model/build ran at once under the
work-cycle resource queue; use an equally exclusive memory budget on a 16 GiB host.

```sh
uv venv --python python3.12 /absolute/task/outline-venv
uv pip install --python /absolute/task/outline-venv/bin/python -r evaluations/document-outline/requirements.lock.txt
/absolute/task/outline-venv/bin/python evaluations/document-outline/tools/generate.py
```

Convert the native Word input using the production image and the current
`LibreOfficeDocumentConverter` arguments. Select the image digest used by your
installation and record `libreoffice --headless --version`; the measured version
and image ID are in `results/libreoffice.json`. Mount this evaluation's corpus at
`/evaluation` and use a unique profile/output directory:

```sh
libreoffice --headless --nologo --nodefault --nolockcheck --norestore \
  -env:UserInstallation=file:///tmp/outline-unique-profile/ \
  --convert-to pdf --outdir /tmp/outline-output /evaluation/heading-styles.docx
```

Copy the resulting `heading-styles.pdf` into `corpus/` without replacing the DOCX.
The comparison intentionally submits that PDF to both backends. MinerU Local can
accept native DOCX; its native output is a different input path and is not measured
here. This experiment does not imply that StructaDoc always converts DOCX.

Before the model commands, fetch only the recorded files at the exact measured
repository revisions (not the latest model branch):

```sh
/absolute/task/outline-venv/bin/python evaluations/document-outline/tools/prepare_models.py --work /absolute/task/outline-models
/absolute/task/outline-venv/bin/python evaluations/document-outline/tools/run_mineru.py \
  --cli /absolute/task/outline-venv/bin/mineru --work /absolute/task/outline-models --backend pipeline
/absolute/task/outline-venv/bin/python evaluations/document-outline/tools/run_mineru.py \
  --cli /absolute/task/outline-venv/bin/mineru --work /absolute/task/outline-models --backend vlm-engine
/absolute/task/outline-venv/bin/python evaluations/document-outline/tools/collect.py --work /absolute/task/outline-models
```

`run_mineru.py` sets task-owned `HF_HOME`, `MODELSCOPE_CACHE`, and absolute
`MINERU_TOOLS_CONFIG_JSON`, disables implicit Hugging Face tokens, and uses public
Hugging Face model downloads. It never changes HOME or global MinerU settings.
Each command is bounded to 600 seconds by default and only its own process group
is terminated on expiry. One failed document stops that backend: remaining files
are unexecuted, not quality scores of zero. Logs and cache revisions remain in
`--work`; `collect.py` preserves small synthetic Content Lists, actual Markdown, and versions.
The CLI starts a temporary localhost API on an ephemeral port; it sends no
source document to an online parsing service. Model acquisition still uses the
public network. These are local MinerU 3.4.5 results, not Cloud results or v4 tiers.

For every collected Content List, `collect.py` writes a ZIP with that list and
the actual generated Markdown under `--work`. Use
current StructaDoc intake/normalization to produce the Blocks used by scoring:

```sh
dotnet run evaluations/document-outline/tools/Normalize.cs -- \
  /absolute/task/outline-models/pipeline-book.zip \
  evaluations/document-outline/results/pipeline/book-blocks.json \
  /absolute/task/normalize-pipeline-book
```

Repeat with each backend/document. Give each invocation a distinct empty storage
root; do not reuse a storage root with different input under the fixed test run ID.
This calls the production normalizer rather than substituting Provider JSON for
the Canonical Model. `normalize_content_list` is only a narrow, tested projection
used to cross-check headings against the actual .NET output.

```sh
/absolute/task/outline-venv/bin/python evaluations/document-outline/tools/score.py
/absolute/task/outline-venv/bin/python -m unittest discover -s evaluations/document-outline/tools -p 'test_*.py' -v
```

## Measurement definitions

All sources and both backends use the same twelve expected nodes and per-file
denominators (3, 3, 3, 3, 0). A matched title is exact after whitespace folding and
case folding. Correct start means the authored title at its one-based physical
page; the fixtures have unique titles, including two on one page. Correct level
means the authored hierarchy level. The joint count requires both. A title
with an incorrect page/level is not a correct joint result. Missing titles and
additional/duplicate titles are reported separately, and extra titles on the
unstructured input are false positives. Start accuracy is not a bounding-box
or end-boundary accuracy claim. Actual Provider starts record canonical Block
`sequence`; missing or ambiguous title anchors cannot be assigned a sequence.

Bookmarks use their declared destination pages and nesting. Printed contents use
literal dot-leader rows, indentation, a unique `Page.label`, and an exact title
on the destination page. Generic numbering uses bounded decimal hierarchy plus
Chapter/Part and a small Chinese prefix grammar; it does not classify prose.
The deterministic results measure PDF-text anchors, independently of Provider
recognition. Provider results measure actual canonical Blocks. A future runtime
implementation still needs unambiguous Block anchors before publishing nodes;
PDF-text success alone cannot promise a usable outline for every Provider.
`results/source-anchors.json` separately records unique matches into the actual
canonical Blocks for both backends; missing or duplicate matches are omitted.

A source absent from a document has zero found nodes; the complete per-document
truth remains the denominator. The summary also shows coverage on documents
that actually declare that source, so absence is not confused with a detection
failure. No interpolation of absent labels, fallback to physical page numbers,
or arbitrary choice among repeated labels is allowed. pypdf's automatic default
labels are explicitly ignored unless the PDF has `/PageLabels` metadata.
