namespace StructaDoc.Application.Conversion;

public sealed class DocumentConversionRequest
{
    public DocumentConversionRequest(
        string sourceMediaType,
        long sourceSizeBytes,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        string outputMediaType = DocumentConversionMediaTypes.Pdf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMediaType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSizeBytes);
        ArgumentNullException.ThrowIfNull(openReadAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputMediaType);

        SourceMediaType = sourceMediaType;
        SourceSizeBytes = sourceSizeBytes;
        OpenReadAsync = openReadAsync;
        OutputMediaType = outputMediaType;
    }

    public string SourceMediaType { get; }

    public long SourceSizeBytes { get; }

    public Func<CancellationToken, Task<Stream>> OpenReadAsync { get; }

    public string OutputMediaType { get; }
}
