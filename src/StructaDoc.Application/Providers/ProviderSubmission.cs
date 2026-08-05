namespace StructaDoc.Application.Providers;

public sealed record ProviderSubmission
{
    public ProviderSubmission(string externalTaskId, TimeSpan? suggestedPollDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTaskId);

        if (suggestedPollDelay.HasValue && suggestedPollDelay.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(suggestedPollDelay));
        }

        ExternalTaskId = externalTaskId;
        SuggestedPollDelay = suggestedPollDelay;
    }

    public string ExternalTaskId { get; }

    public TimeSpan? SuggestedPollDelay { get; }
}
