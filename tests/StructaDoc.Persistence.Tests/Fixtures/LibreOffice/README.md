# LibreOffice Legacy Office Fixtures

These fixtures contain only fixed StructaDoc test strings. They do not contain third-party or private document content.

They were generated on 2026-08-29 in an `ubuntu:24.04` ARM64 container with the same no-GUI LibreOffice component set used by the production `Dockerfile`:

```text
LibreOffice 24.2.7.2 420(Build:2)
```

The DOC and XLS source inputs were a minimal UTF-8 HTML document and CSV workbook. LibreOffice created those checked-in legacy Compound File Binary outputs with these filters:

```bash
libreoffice --headless --convert-to 'doc:MS Word 97' sample.html
libreoffice --headless --convert-to 'xls:MS Excel 97' sample.csv
```

The PPT fixture was created through LibreOffice's Python UNO bridge. The generator opened `private:factory/simpress`, added one `TitleTextShape` containing the fixed test text, and stored the document with the `MS PowerPoint 97` filter. The source content markers were `DOC-LEGACY-TEST`, `XLS-LEGACY-TEST`, and `PPT-LEGACY-TEST`, respectively.

| Fixture | SHA-256 |
|---|---|
| `legacy-word.doc` | `b8d87ea2be74298009aaee9e11d26f05af6685857ef3ae40c42ffe3e3ce18c07` |
| `legacy-spreadsheet.xls` | `d854f881deb13ecd9fb274a24b75dcdf8999ee39c98cb7ef5f8168d9d85045bb` |
| `legacy-presentation.ppt` | `872012a18c6523cf852ef70759899fe2ab61a56fbd9e6bbb259e0eeba883fdcf` |
