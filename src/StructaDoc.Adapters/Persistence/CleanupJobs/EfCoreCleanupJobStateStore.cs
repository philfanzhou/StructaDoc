using Microsoft.EntityFrameworkCore;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Adapters.Persistence.CleanupJobs;

/// <summary>
/// Conditional state transitions for cleanup jobs. A successful claim advances the row's
/// concurrency version, and that version is the attempt's ownership token: every transition this
/// store writes matches both the status and the concurrency version the caller's claim produced,
/// so a row that another worker reclaimed or completed ignores the stale attempt entirely.
/// </summary>
public sealed class EfCoreCleanupJobStateStore(StructaDocDbContext dbContext)
{
    /// <summary>
    /// Schedules the next retry for the attempt identified by <paramref name="claimConcurrencyVersion"/>
    /// — the version its successful claim wrote. One conditional update writes the retry status, the
    /// recorded error, and the retry deadline only while the job is still running under that claim;
    /// any other row state leaves the row untouched and returns <c>false</c>, which makes a lost
    /// claim or an already completed job a harmless no-op for the stale attempt.
    /// </summary>
    public async Task<bool> TryScheduleRetryAsync(
        Guid cleanupJobId,
        long claimConcurrencyVersion,
        string errorMessage,
        DateTime failedAtUtc,
        DateTime retryAtUtc,
        CancellationToken cancellationToken = default)
    {
        var affectedRows = await dbContext.CleanupJobs
            .Where(job => job.Id == cleanupJobId
                && job.Status == CleanupJobStatuses.Running
                && job.ConcurrencyVersion == claimConcurrencyVersion)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, CleanupJobStatuses.RetryWait)
                    .SetProperty(job => job.ErrorMessage, errorMessage)
                    .SetProperty(job => job.NextAttemptAtUtc, retryAtUtc)
                    .SetProperty(job => job.UpdatedAtUtc, failedAtUtc)
                    .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1),
                cancellationToken);
        return affectedRows == 1;
    }
}
