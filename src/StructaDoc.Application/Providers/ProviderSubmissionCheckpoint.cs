namespace StructaDoc.Application.Providers;

public sealed class ProviderSubmissionCheckpoint
{
    public ProviderSubmissionCheckpoint(string externalTaskId, string continuationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationToken);

        if (externalTaskId.Length > 512 || externalTaskId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The external task ID is invalid.",
                nameof(externalTaskId));
        }

        if (!string.Equals(externalTaskId, externalTaskId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The external task ID cannot have leading or trailing whitespace.",
                nameof(externalTaskId));
        }

        if (continuationToken.Length > 8192 || continuationToken.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The submission continuation token is invalid.",
                nameof(continuationToken));
        }

        ExternalTaskId = externalTaskId;
        ContinuationToken = continuationToken;
    }

    public string ExternalTaskId { get; }

    public string ContinuationToken { get; }

    public override string ToString() =>
        $"ProviderSubmissionCheckpoint {{ ExternalTaskId = {ExternalTaskId}, ContinuationToken = [redacted] }}";
}
