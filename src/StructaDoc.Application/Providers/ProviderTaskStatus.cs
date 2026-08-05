namespace StructaDoc.Application.Providers;

public enum ProviderTaskState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Unknown,
}

public sealed record ProviderTaskStatus(
    ProviderTaskState State,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Retryable = false,
    TimeSpan? SuggestedPollDelay = null);
