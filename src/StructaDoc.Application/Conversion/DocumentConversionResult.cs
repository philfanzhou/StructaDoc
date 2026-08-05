namespace StructaDoc.Application.Conversion;

public sealed class DocumentConversionResult : IAsyncDisposable
{
    private readonly Func<ValueTask> disposeAsync;
    private int disposed;

    public DocumentConversionResult(
        string converterType,
        string converterVersion,
        string outputMediaType,
        long sizeBytes,
        Stream content,
        Func<ValueTask>? disposeAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(converterType);
        ArgumentException.ThrowIfNullOrWhiteSpace(converterVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputMediaType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException("Converted document content must be readable.", nameof(content));
        }

        ConverterType = converterType;
        ConverterVersion = converterVersion;
        OutputMediaType = outputMediaType;
        SizeBytes = sizeBytes;
        Content = content;
        this.disposeAsync = disposeAsync ?? (() => ValueTask.CompletedTask);
    }

    public string ConverterType { get; }

    public string ConverterVersion { get; }

    public string OutputMediaType { get; }

    public long SizeBytes { get; }

    public Stream Content { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Content.DisposeAsync();
        }
        finally
        {
            await disposeAsync();
        }
    }
}
