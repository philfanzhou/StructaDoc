# ADR-0014: Serve the Non-Archiving Layout Mode Through MinerU Cloud

- Status: Accepted
- Date: 2026-10-25
- Related issue: [#203](https://github.com/philfanzhou/StructaDoc/issues/203)
- Supersedes in part: [ADR-0013](./0013-non-archiving-layout-detection.md) (points 2 and 4)

## Context

The consumer of the layout mode ([ADR-0013](./0013-non-archiving-layout-detection.md)) needs
page-region candidates for single page images. ADR-0013 chose a dedicated layout-only inference
runtime: the optional Python worker built on the MinerU SDK layout path, deployed beside the main
image. That means every consumer must deploy and feed a second component — model files, Python,
memory sizing, and all — before the first layout request can be answered.

The deployment already runs a MinerU Cloud Provider configuration with a working credential for
document parsing. A direct probe against that service (recorded on issue #203, 2026-10-25)
verified the practical shape of using it for layout:

- The official batch API (`POST /api/v4/file-urls/batch`, signed upload,
  `GET /api/v4/extract-results/batch/{batchId}`, result ZIP) accepts a single JPEG or PNG and
  completes quickly; both probe variants — an upright PNG and an EXIF-orientation-6 JPEG —
  answered with equal main-region geometry in the orientation-applied frame, and both result
  archives were downloadable within seconds.
- The service runs its full extraction pipeline regardless of the submission switches: with
  `is_ocr=false`, `enable_table=false`, and `enable_formula=false`, the answer still contained
  recognized text, table HTML, formula content, and `layout.json` reporting `_ocr_enable=true`.
  There is no layout-only execution mode to select on the cloud side.
- The answer's `layout.json` (the cloud's middle JSON) carries per-page `page_size` and main-block
  pixel boxes over that size, with the blocks' contents nested inside them — usable structured
  geometry, distinct from Markdown and from newly invented coordinates.

ADR-0013 point 2 forbade exactly this: a layout backend must never fall back to "full document
parsing or a cloud extraction API". The maintainer confirmed on 2026-10-25 that this prohibition
was stronger than the requirement needs to be for the cloud route: the consumer needs layout
results from its existing MinerU service, with no local model deployment and no archive in
StructaDoc, and cloud-side OCR, table recognition, formula recognition, and full parsing are
allowed. This decision records that correction.

## Decision

The non-archiving layout-detection mode of ADR-0013 gains a second backend: the configured MinerU
Cloud Provider, used through the official cloud extraction API. ADR-0013 points 2 and 4 are
superseded to exactly this extent — every other point of ADR-0013 stands unchanged, and the
optional local layout runtime remains a supported backend.

1. **Explicit backend selection.** One administrator setting, `Layout:Backend`, selects the
   backend: `layout-runtime` (the default and the shipped state, preserving ADR-0013's runtime
   behavior unchanged when the key is absent) or `mineru-cloud`. The choice is deliberate and
   restart-bound; nothing guesses it from a URL, and no failed runtime request ever falls back to
   the cloud route or vice versa. The cloud route requires no `LayoutRuntime:BaseUrl`, no second
   copy of the cloud credential, no model files, no Python, and no worker container.

2. **Reuse of the configured cloud service.** The cloud backend resolves, per request, the enabled
   default Provider configuration of type `mineru-cloud`, its current immutable version, and its
   credential decrypted through the existing secret protector — the same snapshot shape the Parse
   Run execution context builds, held for that one request with no record of it. An absent,
   disabled, or non-cloud default Provider, or one without its credential, answers the existing
   explicit unsupported-capability configuration error with the specific reason.

3. **The exchange inside the request.** One overall deadline bounds submission, signed upload,
   polling, result download, and normalization; concurrency is bounded; caller cancellation
   propagates. The cloud batch identifier lives in request memory only. The downloaded archive is
   one bounded in-memory buffer. No Document, Parse Run, Page, Block, Asset, Artifact, or
   request-result record is created, and no image, archive, raw JSON, or normalized result is
   stored — the mode stays non-archiving exactly as ADR-0013 point 1 defines it.

4. **Honest geometry from the cloud's structured output.** Regions come from the cloud's
   `layout.json`: main blocks normalized against their `page_size`, with nested line and span
   entries never reported as peer regions, supported generic and peripheral categories preserved,
   and malformed or incompatible output rejected rather than answered as a false empty success.
   The cloud works in the orientation-applied frame; geometry is mapped back to the original
   image's stored frame so the public contract is unchanged and a consumer applying the reported
   EXIF orientation rotates exactly once.

5. **No promise that cloud execution skips recognition.** The cloud service runs its full
   extraction pipeline for the submitted image and retains its own task state; StructaDoc reduces
   the answer to layout regions and says so in the response's bounded diagnostics. Documentation
   and the API description state the external transfer and the full-pipeline behavior rather than
   advertising a layout-only cloud mode. Cloud-side retention is outside this service's
   non-archiving promise, which is about what StructaDoc itself stores.

6. **Separation preserved.** The cloud layout execution is its own implementation type of the
   layout contract. The full-parsing cloud adapter still never implements the layout contract, no
   layout adapter is ever a document-parsing Provider, and the persistent Parse Run orchestration
   is untouched: nothing about the cloud layout path creates or deletes runs, checkpoints, or
   archives.

## Consequences

### Positive

- A deployment with the already-configured MinerU Cloud Provider answers layout requests with one
  setting and no additional component, model download, or worker.
- The optional local runtime remains available for deployments that want inference on their own
  hardware; its behavior and configuration are unchanged.
- The public request/response contract is untouched: same endpoint, scope, limits, response shape.

### Trade-offs

- The cloud route transfers the image to an external party and executes more work than layout
  alone; the response says so, and the runtime backend remains the no-external-transfer choice.
- One more setting and one more backend to document and keep tested.
- Cloud latency and availability bound the request's duration; the overall deadline reports it
  honestly as the existing timeout outcome, and cloud-side task state after a lost response is
  outside this service's control.

## Change Rule

Guaranteeing pure-layout computation inside MinerU Cloud, cloud-side zero retention, or a
synchronous cancellation of cloud tasks would be new promises this decision does not make; making
them requires a new ADR. The non-archiving points of ADR-0013 remain governed by ADR-0013.
