# Synthetic corpus provenance

All document text, outlines, and layout were authored for StructaDoc issue #162 on
2026-10-19. No document is copied from a third party, contains private data, or
uses a Cloud service. The corpus and evaluation code are covered by the
repository's Apache-2.0 license. Fonts in the hand-authored PDFs are PDF standard
Helvetica; Word uses its default theme fonts, substituted by the production
image's fonts during conversion. No font files or model weights are distributed.

`tools/generate.py` creates four PDFs and one native DOCX. `expected.json` was
written before running MinerU and is not generated from Provider output. The
Word PDF is a separate conversion artifact; its original DOCX is retained.
PDF and DOCX generation is byte reproducible with the pinned Python dependencies.
LibreOffice adds export timestamps/IDs, so a fresh Word PDF need not be byte
identical: its text, hierarchy, and destination pages are the reproducible facts.
`results/corpus-sha256.json` identifies the exact PDF bytes both backends received.

| Document | Original | Purpose | Expected nodes |
|---|---|---|---:|
| Book | `corpus/book.pdf` | Nested PDF bookmarks, two headings on page 1 | 3 |
| Printed contents | `corpus/printed-toc.pdf` | No bookmarks; indented contents entries and explicit PDF page labels | 3 |
| Technical report | `corpus/numbered-report.pdf` | Decimal section numbering with a nested section | 3 |
| Word headings | `corpus/heading-styles.docx` | Heading 1/2 styles; compared as `corpus/heading-styles.pdf` | 3 |
| Unstructured prose | `corpus/unstructured.pdf` | Equal-size prose without heading/numbering declarations | 0 |

The twelve authored headings cover a deliberately small sanity corpus, not a
statistical benchmark. There are no scanned, multilingual, malformed, deeply
nested, or adversarial PDFs in the model comparison. Chinese/roman/English
numbering examples and missing/repeated page labels are scoring unit tests only,
not measured model inputs. Synthetic body paragraphs repeat to make headings
visually distinct; this is an easy case and cannot justify general accuracy claims.
