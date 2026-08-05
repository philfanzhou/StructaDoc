namespace StructaDoc.Application.Providers;

public enum ProviderFailureCategory
{
    Transient,
    Configuration,
    Input,
    Permanent,
    Security,
}

public sealed class ProviderException : Exception
{
    public ProviderException(
        string errorCode,
        string safeMessage,
        ProviderFailureCategory category,
        Exception? innerException = null)
        : base(ValidateSafeMessage(safeMessage), innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        if (errorCode.Length > 128)
        {
            throw new ArgumentException(
                "The Provider error code cannot exceed 128 characters.",
                nameof(errorCode));
        }

        ErrorCode = errorCode;
        Category = category;
    }

    public string ErrorCode { get; }

    public ProviderFailureCategory Category { get; }

    public bool Retryable => Category == ProviderFailureCategory.Transient;

    private static string ValidateSafeMessage(string safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);

        if (safeMessage.Length > 2048)
        {
            throw new ArgumentException(
                "The safe Provider error message cannot exceed 2048 characters.",
                nameof(safeMessage));
        }

        return safeMessage;
    }
}
