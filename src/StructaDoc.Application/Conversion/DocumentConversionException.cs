namespace StructaDoc.Application.Conversion;

public sealed class DocumentConversionException(
    string errorCode,
    string safeMessage,
    bool retryable = false,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string ErrorCode { get; } = errorCode;

    public bool Retryable { get; } = retryable;
}
