# Office-to-PDF Conversion

This note describes the LibreOffice adapter, executor integration, and recovery boundary. Deployment follows [ADR-0003](../adr/0003-technology-and-single-image-deployment.md); converted Artifact semantics follow the [Canonical Document Model](../specifications/canonical-document-model.md).

## Execution Strategy

`ParseRunExecutor` first loads the capability snapshot from the immutable Provider configuration version. It submits the original when supported. Only when the source format is unsupported, the Provider accepts PDF, and a registered converter supports the source does it create a PDF fallback.

The LibreOffice adapter supports DOC, DOCX, XLS, XLSX, PPT, and PPTX to PDF. It does not ask LibreOffice to guess arbitrary formats.

MinerU Local reports the OOXML formats DOCX, XLSX, and PPTX as native inputs, so those originals are submitted without conversion. Its native capability intentionally excludes the legacy binary DOC, XLS, and PPT formats. StructaDoc still accepts and retains those uploads, then converts them to PDF through this fallback before Local submission.

Conversion uses the `converting` stage:

1. stream the original into an isolated work directory while rechecking input size;
2. start LibreOffice headless directly from .NET;
3. stream the output to an immutable key such as `parse-runs/{parseRunId}/conversions/{artifactId}.pdf`;
4. under the live lease, atomically store the conversion snapshot, set `submittedMediaType` to `application/pdf`, and enter `preparing-source`;
5. reuse that snapshot for Provider submission and include the same ID as a `normalized-pdf` Artifact in the final bundle.

The original is never overwritten. The snapshot records converter type/version, source/output media types, Artifact ID, size, SHA-256, and internal reference. It excludes temporary paths and command-line details. Artifact metadata contains only non-sensitive converter and format facts.

If the process exits after object creation but before snapshot commit, the random object is not reused and later orphan reconciliation may remove it. Once committed, crash recovery reuses the saved PDF and never runs LibreOffice again for that Parse Run.

## Process and Resource Boundary

Each conversion has a distinct work directory, output directory, and LibreOffice user profile. `ProcessStartInfo.ArgumentList` with `UseShellExecute=false` prevents user input from forming a shell command.

The adapter enforces:

- a global conversion semaphore;
- input and output byte limits;
- a timeout and periodic work-directory size checks;
- process-tree termination on timeout or cancellation;
- rejection of non-zero exit, missing/empty/oversized output, or a non-`%PDF-` signature;
- at most 16 KiB each of captured stdout and stderr, neither exposed in logs or API errors;
- work-directory cleanup after success, failure, or cancellation.

Container CPU, memory, process, and filesystem quotas remain necessary in addition to application limits.

## Configuration

| Key | Default | Meaning |
|---|---:|---|
| `LibreOffice:Enabled` | `true` | Allows the Office fallback |
| `LibreOffice:ExecutablePath` | `libreoffice` | Direct executable path or name |
| `LibreOffice:TemporaryPath` | `./data/temp/libreoffice` | Parent for isolated work directories |
| `LibreOffice:MaxConcurrency` | `1` | Concurrent conversions per Host |
| `LibreOffice:Timeout` | 3 minutes | Per-process limit |
| `LibreOffice:ResourceInspectionInterval` | 250 ms | Temporary usage check interval |
| `LibreOffice:MaxInputBytes` | 100 MiB | Conversion input limit |
| `LibreOffice:MaxOutputBytes` | 200 MiB | PDF output limit |
| `LibreOffice:MaxTemporaryBytes` | 512 MiB | Combined input, output, and profile disk limit |

Environment variables use double underscores, for example `LibreOffice__ExecutablePath=/usr/bin/libreoffice`. Conversion runs as part of an execution attempt, so it happens only once a Provider is configured and a Parse Run is created against it.

## Verification and Remaining Work

Unit tests use a fake process runner to cover argument construction, isolated profiles, input limits, invalid PDFs, and cleanup without requiring LibreOffice on a developer machine. Separate environment-gated integration tests check in minimal real DOC, XLS, and PPT Compound File Binary fixtures, verify their detected media types and hashes, and pass them through the real `LibreOfficeDocumentConverter` and `LibreOfficeProcessRunner`. The integration asserts a non-empty `%PDF-` output, the actual LibreOffice version, unchanged inputs, and work-directory cleanup.

GitHub Actions installs the same Writer, Calc, Impress, Math, and Core no-GUI components as the production image and enables those integration tests explicitly. The default solution test command leaves them skipped, so LibreOffice is not a local development prerequisite.

Deployment owners should still run representative production documents to verify the deployed LibreOffice build, locale, and font set against their own formatting requirements. The checked-in fixtures prove basic legacy import and PDF export, not fidelity for every real-world document.
