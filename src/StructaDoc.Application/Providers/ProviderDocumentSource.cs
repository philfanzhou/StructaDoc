namespace StructaDoc.Application.Providers;

public sealed class ProviderDocumentSource
{
    public ProviderDocumentSource(
        string fileName,
        string mediaType,
        long sizeBytes,
        Func<CancellationToken, Task<Stream>> openReadAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentNullException.ThrowIfNull(openReadAsync);

        FileName = fileName;
        MediaType = mediaType;
        SizeBytes = sizeBytes;
        OpenReadAsync = openReadAsync;
    }

    public string FileName { get; }

    public string MediaType { get; }

    public long SizeBytes { get; }

    public Func<CancellationToken, Task<Stream>> OpenReadAsync { get; }
}
