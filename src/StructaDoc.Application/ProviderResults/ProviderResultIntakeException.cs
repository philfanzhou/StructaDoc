using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ProviderResults;

public sealed class ProviderResultIntakeException : Exception
{
    public ProviderResultIntakeException(
        string errorCode,
        string safeMessage,
        ProviderFailureCategory category,
        Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);

        if (errorCode.Length > 128)
        {
            throw new ArgumentException(
                "The result intake error code cannot exceed 128 characters.",
                nameof(errorCode));
        }

        if (safeMessage.Length > 2048)
        {
            throw new ArgumentException(
                "The safe result intake message cannot exceed 2048 characters.",
                nameof(safeMessage));
        }

        ErrorCode = errorCode;
        Category = category;
    }

    public string ErrorCode { get; }

    public ProviderFailureCategory Category { get; }

    public bool Retryable => Category == ProviderFailureCategory.Transient;
}
