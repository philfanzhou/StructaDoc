namespace StructaDoc.Application.ParseRuns;

public interface IParseRunLeaseStore
{
    Task<ParseRunLease?> TryClaimNextAsync(
        string workerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<ParseRunLease?> TryRenewLeaseAsync(
        ParseRunLease currentLease,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<int> RequeueExpiredUnstartedClaimsAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default);
}
