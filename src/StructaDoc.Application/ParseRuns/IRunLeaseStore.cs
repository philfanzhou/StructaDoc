namespace StructaDoc.Application.ParseRuns;

public interface IRunLeaseStore
{
    Task<RunLease?> TryClaimNextAsync(
        string workerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<RunLease?> TryRenewLeaseAsync(
        RunLease currentLease,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<RunLease?> TryRecoverNextRunningAsync(
        string workerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<int> RequeueExpiredUnstartedClaimsAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default);

    Task<RunUnsubmittedRecovery> RecoverExpiredUnsubmittedRunsAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default);
}
