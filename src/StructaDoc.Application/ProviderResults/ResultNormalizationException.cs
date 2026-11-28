using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ProviderResults;

public sealed class ResultNormalizationException : Exception
{
    public ResultNormalizationException(
        string errorCode,
        string message,
        ProviderFailureCategory category,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
        Category = category;
    }

    public string ErrorCode { get; }

    public ProviderFailureCategory Category { get; }

    public bool Retryable => Category == ProviderFailureCategory.Transient;
}
