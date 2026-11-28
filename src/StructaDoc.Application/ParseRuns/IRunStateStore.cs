namespace StructaDoc.Application.ParseRuns;

public interface IRunStateStore
{
    Task<RunLease?> TryStartAsync(
        RunLease currentLease,
        string initialStage,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<RunLease?> TryUpdateStageAsync(
        RunLease currentLease,
        string stage,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<RunLease?> TryRecordProviderSubmissionAsync(
        RunLease currentLease,
        string externalTaskId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<RunFailureTransition?> TryRecordFailureAsync(
        RunLease currentLease,
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

    /// <summary>
    /// Completes cancellation for runs that no Worker can still be executing, either because they
    /// never held a lease or because their lease has lapsed.
    /// </summary>
    Task<int> FinalizeAbandonedCancellationsAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes cancellation for a run the calling Worker still claims. The Worker has already
    /// stopped local execution, so it does not need to wait for its own lease to lapse.
    /// </summary>
    Task<bool> TryFinalizeOwnedCancellationAsync(
        Guid parseRunId,
        string workerId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
