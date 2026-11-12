# ADR-0015: Non-Archiving Image Text Recognition

- Status: Accepted
- Date: 2026-11-11
- Related: [ADR-0013](./0013-non-archiving-layout-detection.md) (its change rule requires a new ADR
  for content recognition), [ADR-0014](./0014-cloud-layout-backend.md) (the cloud exchange this
  mode reuses)

## Context

A consumer, Ruoyu.Study, wants teachers and students to photograph a printed word list (each row
an English word, often a part of speech, and a Chinese definition) and turn it into candidate
entries that a person confirms. The consumer needs the recognized text of one image, with enough position
information to keep each row's word and definition together. It does not want the photo kept as a
document, and it needs the answer in the same request, while a teacher waits.

StructaDoc can already do most of this, but no public route returns it:

- The document lifecycle (upload, durable Parse Run, Blocks) returns recognized text, but archives
  the original and every result, and the caller has to poll. Classroom photos are not documents the
  consumer wants retained.
- `POST /api/v1/layout/detect` answers in the same request and archives nothing, but ADR-0013
  point 3 forbids recognized text in its response. With `Layout:Backend = mineru-cloud`
  (ADR-0014) the cloud has already recognized the text — its `layout.json` carries
  `lines[].spans[]` content — and StructaDoc discards it.

ADR-0013's change rule says that extending the layout mode to content recognition needs a new ADR.
Changing what `layout:detect` returns would also widen what every existing layout client receives
without its operator choosing it.

## Decision

StructaDoc adds a second non-archiving mode, image text recognition, beside the layout mode. It is
a separate endpoint with a separate scope; the layout mode and its contract are unchanged.

1. **Separate route and scope.** `POST /api/v1/image/recognize` accepts one JPEG or PNG, under the
   same input rules and limits as the layout endpoint (signature-based format check, byte and
   pixel bounds, EXIF orientation). It requires the new API-client scope `image:recognize`, with no
   interactive-session branch. Holding `layout:detect` does not grant it, and holding
   `image:recognize` does not grant layout detection.

2. **Explicitly enabled, cloud only.** An administrator setting, `Recognition:Enabled` (default
   false, restart-bound like `Layout:Backend`), turns the mode on. When it is off, the route answers
   the explicit unsupported-capability configuration error (`503`, under the route's own code
   `recognition-capability-unsupported`) and makes no outbound request.
   When it is on, it resolves the enabled default MinerU Cloud Provider configuration per request,
   exactly as ADR-0014 point 2 does, independently of `Layout:Backend`. The optional local layout
   runtime cannot serve this mode, because it detects layout only; no request ever falls back from
   one backend to another.

3. **Same non-archiving exchange.** The cloud exchange is the one ADR-0014 point 3 defines: one
   overall deadline, bounded concurrency, caller cancellation, the archive in one bounded in-memory
   buffer, and the cloud batch identifier in request memory only. No Document, Parse Run, Page,
   Block, Asset, Artifact, or request-result record is created, and no image, archive, raw JSON, or
   result is stored. Cloud-side retention stays outside this promise, as in ADR-0014 point 5. The
   bound on concurrent cloud exchanges is one budget shared with the cloud layout backend, because
   both modes spend the same Provider account.

4. **Provider-neutral answer built from Canonical Model terms.** The response keeps the layout
   response's frame fields — request ID, stored width and height, `top-left-origin-normalized`
   coordinate frame, EXIF orientation, engine, model, version, bounded diagnostics — and returns
   **blocks in reading order** instead of regions. Each block uses the Canonical Model's Block
   vocabulary (Section 8): `sequence`, `type`, `content`, `contentFormat`, and a normalized `bbox`
   mapped back to the stored frame as ADR-0014 point 4 maps regions. Text-bearing blocks also carry
   `lines`, each with its `text` and normalized `bbox`, in the order the backend reports them, so a
   caller can keep one printed row together. Tables carry their recognized table as `html` content
   and formulas as `latex`. Caption and footnote text the backend attaches to a table, figure, or
   code block goes into the Block's `caption` and `footnote` fields, never into separate blocks.
   Figures carry geometry only; no cropped assets, download URLs, or Markdown are returned. All
   recognized text is untrusted; callers escape or sanitize it before rendering.

5. **Honest output.** Malformed or incompatible cloud output is an error, never a false empty
   success; a page with no blocks is a valid empty answer. Recognition quality, confidence, and
   reading order are what the backend produces; StructaDoc adds no correction, language detection,
   or domain interpretation. Logs carry request identifiers, timing, counts, and status only — never
   image bytes, recognized text, raw responses, credentials, or signed URLs.

6. **Product boundary.** This is the second explicit exception to the retained-document lifecycle,
   alongside ADR-0013. It returns recognized text only. Extracting domain entities from that text —
   vocabulary entries, questions, invoice fields — stays with the caller, as ADR-0001 requires.

## Consequences

### Positive

- A deployment that already has the MinerU Cloud Provider answers recognition with one setting,
  one API client, and no new component.
- The layout mode, its scope, and every existing layout client are unchanged.
- Callers receive text with row-level positions without creating a document lifecycle per photo.

### Trade-offs

- The image goes to an external service; documentation and the API description must say so, as for
  ADR-0014.
- One more route, scope, setting, and response contract to keep tested; the cloud reduction must
  now preserve span text it previously discarded.
- Cloud latency bounds the request, so callers must expect responses in seconds and handle the
  timeout outcome.
- Deployments that use only the local layout runtime cannot offer recognition.

## Change Rule

Adding a local recognition backend, persistent image ingestion, an archive of requests, a
submit-and-poll protocol, or domain extraction requires a new ADR. Changes to the layout mode
remain governed by ADR-0013 and ADR-0014.
