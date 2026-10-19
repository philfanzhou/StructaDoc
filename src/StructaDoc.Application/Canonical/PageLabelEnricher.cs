using StructaDoc.Application.Providers;

namespace StructaDoc.Application.Canonical;

/// <summary>
/// Adds printed page labels to a normalized bundle. It runs after normalization, on the complete
/// bundle and the complete submitted document, so every Provider and both the normal and the
/// segmented large-PDF commit paths get the same labels. It keeps no state between calls.
/// </summary>
public sealed class PageLabelEnricher(IPdfPageLabelReader pdfReader)
{
    private const string PdfMediaType = "application/pdf";

    /// <summary>
    /// Returns the bundle with every Page's label set from the submitted PDF's page-label tree
    /// when it gives at least one usable label, otherwise from consistent <c>page-number</c>
    /// Blocks, otherwise null. A PDF that cannot be parsed or has a malformed tree never fails the
    /// call; storage failures while reading the document and cancellation propagate.
    /// </summary>
    public async Task<PageLabelEnrichmentResult> EnrichAsync(
        ParseBundle bundle,
        ProviderDocumentSource submittedDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(submittedDocument);
        cancellationToken.ThrowIfCancellationRequested();

        PdfPageLabelReadStatus? pdfStatus = null;
        if (string.Equals(submittedDocument.MediaType, PdfMediaType, StringComparison.OrdinalIgnoreCase))
        {
            var read = await pdfReader.ReadAsync(submittedDocument.OpenReadAsync, cancellationToken);
            pdfStatus = read.Status;
            if (read.Status == PdfPageLabelReadStatus.Valid)
            {
                var fromPdf = PageLabelRules.ApplyPdfPageLabels(bundle.Pages, read.Ranges);
                if (fromPdf.Any(page => page.Label is not null))
                {
                    return new PageLabelEnrichmentResult(bundle with { Pages = fromPdf }, pdfStatus);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fromBlocks = PageLabelRules.ApplyPageNumberBlocks(bundle.Pages, bundle.Blocks);
        return new PageLabelEnrichmentResult(bundle with { Pages = fromBlocks }, pdfStatus);
    }
}

/// <param name="Bundle">The bundle with Page labels set.</param>
/// <param name="PdfStatus">
/// What reading the submitted PDF's page-label tree found, or <see langword="null"/> when the
/// submitted document is not a PDF.
/// </param>
public sealed record PageLabelEnrichmentResult(
    ParseBundle Bundle,
    PdfPageLabelReadStatus? PdfStatus);
