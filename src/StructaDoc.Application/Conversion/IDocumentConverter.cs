namespace StructaDoc.Application.Conversion;

public interface IDocumentConverter
{
    bool Supports(string sourceMediaType, string outputMediaType);

    Task<DocumentConversionResult> ConvertAsync(
        DocumentConversionRequest request,
        CancellationToken cancellationToken = default);
}
