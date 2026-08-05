namespace StructaDoc.Application.ParseRuns;

public interface IParseRunStateStore
{
    Task<ParseRunLease?> TryStartAsync(
        ParseRunLease currentLease,
        string initialStage,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<ParseRunFailureTransition?> TryRecordFailureAsync(
        ParseRunLease currentLease,
        string errorCode,
        string? errorMessage,
        bool retryable,
        DateTime nextAttemptAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<int> QueueDueRetriesAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default);
}
