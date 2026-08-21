# Result API, Exports, and Resource Lifecycle

The public API returns StructaDoc canonical DTOs only: Parse Runs, Pages, Blocks, Assets, and Artifact metadata. Provider raw JSON, internal `storageRef` values, checkpoints, external task IDs, and credentials never appear in normal responses. Binary content is available only through authorized download routes.

```text
GET    /api/v1/documents/{documentId}/parse-runs
GET    /api/v1/parse-runs/{parseRunId}
GET    /api/v1/parse-runs/{parseRunId}/pages
GET    /api/v1/parse-runs/{parseRunId}/blocks
GET    /api/v1/parse-runs/{parseRunId}/assets
GET    /api/v1/parse-runs/{parseRunId}/assets/{assetId}/content
GET    /api/v1/parse-runs/{parseRunId}/artifacts
GET    /api/v1/parse-runs/{parseRunId}/artifacts/{artifactId}/content
GET    /api/v1/parse-runs/{parseRunId}/markdown
GET    /api/v1/parse-runs/{parseRunId}/markdown/preview
GET    /api/v1/parse-runs/{parseRunId}/exports/{markdown|html|zip|pdf}
POST   /api/v1/parse-runs/{parseRunId}/cancel
DELETE /api/v1/parse-runs/{parseRunId}
DELETE /api/v1/documents/{documentId}
```

Blocks use `afterSequence` for stable cursor pagination, and `nextSequence` in the response is the cursor for the page after it. A response without `nextSequence` is the last page; a caller that reads `items` and ignores the cursor sees the beginning of a result with nothing saying it stopped. `pageNumber` narrows the listing to one page, which is what a layout view reads rather than paging the whole run to find one page's Blocks. Their public DTO excludes `ProviderDataJson` and raw source locators.

## Reading a Result

`markdown` returns the canonical Markdown Artifact as it was stored. `markdown/preview` returns the same result rendered as a self-contained HTML page, for display rather than for saving. It is the HTML export byte for byte — the same renderer, link rewriting, and bounded image inlining — and differs in two ways that are the reason it is a separate route:

- it is served inline rather than as an attachment, because a browser that downloads a preview has not shown one;
- it is admitted by read access to the Parse Run rather than the `export` permission, which withholds nothing here because the bytes are the same. See [What `export` Gates](#what-export-gates).

Both `markdown/preview` and the download routes answer with a sandboxed, deny-by-default
Content-Security-Policy: `default-src 'none'` permits only inlined `data:` images and the export's own
inline style, while `base-uri`, forms, and frames remain disabled. They also send
`Referrer-Policy: no-referrer`. HTML rendering disables Provider-authored raw HTML and removes every
image source that was not successfully inlined, so opening a preview cannot make the browser contact
an external or internal host. A result whose images exceed the inlining budget previews with those
images missing; they remain downloadable through the Asset routes.

The preview's ETag is derived before rendering from the Markdown hash, the hashes of Assets eligible for inlining in stable order, the renderer version, and the inlining budgets. A matching `If-None-Match` therefore returns `304` without opening or rendering result content. Rendering changes increment the version so an unchanged Parse Run cannot validate HTML produced by older rules.

## Exports

Provider Markdown references images by the Provider's own archive layout, typically `images/<name>`, which resolves to nothing once the Markdown leaves that archive. Exports therefore translate image links onto the resources they actually ship:

- **markdown** returns the canonical Markdown Artifact byte for byte. It is the traceable artifact, so it is never rewritten; its links stay Provider-relative.
- **zip** contains `document.md` plus authorized Assets under `assets/`, with the bundled Markdown's image links pointing at those entries. Colliding Asset file names are disambiguated by Asset ID.
- **html** renders the Markdown to a single self-contained page with Assets inlined as `data:` URIs, bounded per Asset and in total; Assets beyond the budget and unresolved image sources are omitted rather than fetched.
- **pdf** returns the `normalized-pdf` Artifact when an Office source required conversion, and otherwise the original Document when it is already a PDF. A Parse Run with neither returns `409`.

### What `export` Gates

`export` is not a confidentiality boundary. Every byte an export produces is already reachable with read access: `exports/markdown` is the canonical Markdown Artifact that `markdown` returns, `exports/html` is the page `markdown/preview` renders, `exports/zip` bundles Assets each downloadable from `assets/{assetId}/content`, and `exports/pdf` returns the `normalized-pdf` Artifact or the original Document, both readable in their own right. A grant that gives `read` and withholds `export` does not stop the grantee from obtaining the result; it stops them from asking for it in one packaged piece.

That is the distinction worth granting separately, and it is a real one. A caller reading Blocks through the result API and a caller pulling a run down as a zip are doing different things to the same data, and a deployment may want the second one granted deliberately, metered, and visible in an audit trail. What it is not is a way to let someone read a result without letting them keep a copy — a reader can already assemble one, and `markdown` hands them the canonical file as an attachment.

Link rewriting matches on file name, because the canonical Asset display name is the archive entry's final segment. A file name shared by more than one Asset is ambiguous and is left untouched rather than guessed. ZIP preserves unresolved references for traceability; HTML removes them after rewriting because a self-contained browser document must not fetch them. Absolute and `data:` targets are never mapped onto StructaDoc Assets, and only generated `data:` image targets survive HTML rendering.

Every route performs resource-level authorization. Administrators use administrative policy. Every other caller accesses what it owns or was explicitly shared, and that includes API clients: a scope decides which verbs a key may use, ownership decides which resources, and holding `parses:read` does not make another principal's Parse Run readable. See [ADR-0008](../adr/0008-api-client-resource-isolation.md). A resource outside the caller's authorization boundary is not distinguishable through storage metadata.

## Durable Deletion

Deletion is not a fragile synchronous transaction across a database and object store:

1. the API transaction marks the target `deletion-pending` and writes a unique persistent Cleanup Job containing a complete object-reference snapshot;
2. reads stop exposing the pending resource;
3. the cleanup Worker idempotently deletes originals, converted PDFs, Provider archives, PDF segments, segment archives, Assets, and Artifacts;
4. only after all object deletions succeed does a database transaction remove relational rows and mark the job `completed`;
5. transient failures enter exponential `retry-wait`, and stale `running` jobs are recovered.

A Parse Run is deletable on its own terms once it is final, whether it succeeded, failed, or was cancelled, and whether or not it is the last one its Document has. Deleting a succeeded run removes everything that run produced — Pages, Blocks, Assets, Artifacts, and segments in the database, and the images, canonical Markdown, Provider archive, PDF segments, segment archives, and converted PDF in storage — while leaving the Document and its original file untouched. A Document that loses its last Parse Run is an unparsed Document again and can be parsed afresh. Local storage prunes the directories a deleted run emptied, so nothing survives the run but the tree it shared with others.

A non-final Parse Run cannot be deleted, and a Document with active Parse Runs cannot be deleted. This prevents cleanup and execution Workers from racing for the same resources. Cancellation is therefore the supported way to release a Document whose Parse Run will never complete on its own, including every run created on a Host started without Workers. See [Parse Job Lifecycle](../specifications/parse-job-lifecycle.md) section 13.

The active-state check and Parse Run insertion are guarded by the same Document concurrency version
that deletion advances. Consequently a creation racing deletion either commits first and makes the
deletion retry observe an active Run, or loses and reports the Document unavailable; a Cleanup Job
can never acquire new work after its object-reference snapshot was taken.

Persistent Cleanup Jobs make failed deletion observable and retryable; they do not hide object-storage failures behind prematurely removed database rows.
