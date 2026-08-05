namespace StructaDoc.Application.Storage;

public sealed class FileSizeLimitExceededException(long maxBytes)
    : Exception($"The file exceeds the configured limit of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
