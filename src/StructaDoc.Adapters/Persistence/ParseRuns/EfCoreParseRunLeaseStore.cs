using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Adapters.Persistence.ParseRuns;

public sealed class EfCoreParseRunLeaseStore(StructaDocDbContext dbContext)
    : IParseRunLeaseStore
{
    private const int CandidateBatchSize = 32;

    public async Task<ParseRunLease?> TryClaimNextAsync(
        string workerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkerId(workerId);
        ValidateLeaseArguments(nowUtc, leaseDuration);

        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        var candidates = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun =>
                parseRun.Status == ParseRunStatuses.Queued
                && parseRun.LifecycleState == ResourceLifecycleStates.Active
                && parseRun.Document.LifecycleState == ResourceLifecycleStates.Active
                && parseRun.NextAttemptAtUtc <= nowUtc)
            .OrderBy(parseRun => parseRun.NextAttemptAtUtc)
            .ThenBy(parseRun => parseRun.CreatedAtUtc)
            .ThenBy(parseRun => parseRun.Id)
            .Select(parseRun => new
            {
                parseRun.Id,
                parseRun.ConcurrencyVersion,
            })
            .Take(CandidateBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var affectedRows = await dbContext.ParseRuns
                .Where(parseRun =>
                    parseRun.Id == candidate.Id
                    && parseRun.Status == ParseRunStatuses.Queued
                    && parseRun.LifecycleState == ResourceLifecycleStates.Active
                    && parseRun.Document.LifecycleState == ResourceLifecycleStates.Active
                    && parseRun.NextAttemptAtUtc <= nowUtc
                    && parseRun.ConcurrencyVersion == candidate.ConcurrencyVersion)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.Claimed)
                        .SetProperty(parseRun => parseRun.ClaimedBy, workerId)
                        .SetProperty(parseRun => parseRun.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                        .SetProperty(
                            parseRun => parseRun.AttemptCount,
                            parseRun => parseRun.AttemptCount + 1)
                        .SetProperty(
                            parseRun => parseRun.ConcurrencyVersion,
                            parseRun => parseRun.ConcurrencyVersion + 1),
                    cancellationToken);

            if (affectedRows == 1)
            {
                return new ParseRunLease(
                    candidate.Id,
                    workerId,
                    candidate.ConcurrencyVersion + 1,
                    leaseExpiresAtUtc);
            }
        }

        return null;
    }

    public async Task<ParseRunLease?> TryRenewLeaseAsync(
        ParseRunLease currentLease,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ValidateWorkerId(currentLease.WorkerId);
        ValidateLeaseArguments(nowUtc, leaseDuration);

        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc
                && (parseRun.Status == ParseRunStatuses.Claimed
                    || parseRun.Status == ParseRunStatuses.Running))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(parseRun => parseRun.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken);

        return affectedRows == 1
            ? currentLease with
            {
                ConcurrencyVersion = currentLease.ConcurrencyVersion + 1,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
            }
            : null;
    }

    public async Task<ParseRunLease?> TryRecoverNextRunningAsync(
        string workerId,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkerId(workerId);
        ValidateLeaseArguments(nowUtc, leaseDuration);

        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        var candidates = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun =>
                parseRun.Status == ParseRunStatuses.Running
                && parseRun.ExternalTaskId != null
                && parseRun.LeaseExpiresAtUtc <= nowUtc)
            .OrderBy(parseRun => parseRun.LeaseExpiresAtUtc)
            .ThenBy(parseRun => parseRun.Id)
            .Select(parseRun => new
            {
                parseRun.Id,
                parseRun.ConcurrencyVersion,
            })
            .Take(CandidateBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var affectedRows = await dbContext.ParseRuns
                .Where(parseRun =>
                    parseRun.Id == candidate.Id
                    && parseRun.Status == ParseRunStatuses.Running
                    && parseRun.ExternalTaskId != null
                    && parseRun.LeaseExpiresAtUtc <= nowUtc
                    && parseRun.ConcurrencyVersion == candidate.ConcurrencyVersion)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(parseRun => parseRun.ClaimedBy, workerId)
                        .SetProperty(parseRun => parseRun.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                        .SetProperty(
                            parseRun => parseRun.ConcurrencyVersion,
                            parseRun => parseRun.ConcurrencyVersion + 1),
                    cancellationToken);

            if (affectedRows == 1)
            {
                return new ParseRunLease(
                    candidate.Id,
                    workerId,
                    candidate.ConcurrencyVersion + 1,
                    leaseExpiresAtUtc);
            }
        }

        return null;
    }

    public async Task<int> RequeueExpiredUnstartedClaimsAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        var candidates = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun =>
                parseRun.Status == ParseRunStatuses.Claimed
                && parseRun.ExternalTaskId == null
                && parseRun.LeaseExpiresAtUtc <= nowUtc)
            .OrderBy(parseRun => parseRun.LeaseExpiresAtUtc)
            .ThenBy(parseRun => parseRun.Id)
            .Select(parseRun => new
            {
                parseRun.Id,
                parseRun.ConcurrencyVersion,
            })
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        var recoveredCount = 0;

        foreach (var candidate in candidates)
        {
            recoveredCount += await dbContext.ParseRuns
                .Where(parseRun =>
                    parseRun.Id == candidate.Id
                    && parseRun.Status == ParseRunStatuses.Claimed
                    && parseRun.ExternalTaskId == null
                    && parseRun.LeaseExpiresAtUtc <= nowUtc
                    && parseRun.ConcurrencyVersion == candidate.ConcurrencyVersion)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.Queued)
                        .SetProperty(parseRun => parseRun.ClaimedBy, (string?)null)
                        .SetProperty(parseRun => parseRun.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(
                            parseRun => parseRun.ProtectedSubmissionContinuation,
                            (string?)null)
                        .SetProperty(
                            parseRun => parseRun.ConcurrencyVersion,
                            parseRun => parseRun.ConcurrencyVersion + 1),
                    cancellationToken);
        }

        return recoveredCount;
    }

    public async Task<ParseRunUnsubmittedRecovery> RecoverExpiredUnsubmittedRunsAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        var candidates = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun =>
                parseRun.Status == ParseRunStatuses.Running
                && parseRun.ExternalTaskId == null
                && parseRun.LeaseExpiresAtUtc <= nowUtc)
            .OrderBy(parseRun => parseRun.LeaseExpiresAtUtc)
            .ThenBy(parseRun => parseRun.Id)
            .Select(parseRun => new
            {
                parseRun.Id,
                parseRun.Stage,
                parseRun.ConcurrencyVersion,
            })
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        var requeuedCount = 0;
        var failedCount = 0;
        foreach (var candidate in candidates)
        {
            var submissionOutcomeUnknown = candidate.Stage == ParseRunStages.Submitting;
            var affectedRows = await dbContext.ParseRuns
                .Where(parseRun =>
                    parseRun.Id == candidate.Id
                    && parseRun.Status == ParseRunStatuses.Running
                    && parseRun.ExternalTaskId == null
                    && parseRun.LeaseExpiresAtUtc <= nowUtc
                    && parseRun.ConcurrencyVersion == candidate.ConcurrencyVersion)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            parseRun => parseRun.Status,
                            submissionOutcomeUnknown
                                ? ParseRunStatuses.Failed
                                : ParseRunStatuses.Queued)
                        .SetProperty(
                            parseRun => parseRun.ErrorCode,
                            submissionOutcomeUnknown
                                ? "provider-submission-outcome-unknown"
                                : null)
                        .SetProperty(
                            parseRun => parseRun.ErrorMessage,
                            submissionOutcomeUnknown
                                ? "The Provider submission outcome is unknown and cannot be retried safely."
                                : null)
                        .SetProperty(
                            parseRun => parseRun.ProtectedSubmissionContinuation,
                            (string?)null)
                        .SetProperty(parseRun => parseRun.ClaimedBy, (string?)null)
                        .SetProperty(parseRun => parseRun.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(
                            parseRun => parseRun.NextAttemptAtUtc,
                            nowUtc)
                        .SetProperty(
                            parseRun => parseRun.CompletedAtUtc,
                            submissionOutcomeUnknown ? nowUtc : null)
                        .SetProperty(
                            parseRun => parseRun.ConcurrencyVersion,
                            parseRun => parseRun.ConcurrencyVersion + 1),
                    cancellationToken);

            if (affectedRows == 1)
            {
                if (submissionOutcomeUnknown)
                {
                    failedCount++;
                }
                else
                {
                    requeuedCount++;
                }
            }
        }

        return new ParseRunUnsubmittedRecovery(requeuedCount, failedCount);
    }

    private static void ValidateLeaseArguments(DateTime nowUtc, TimeSpan leaseDuration)
    {
        ValidateUtc(nowUtc);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
    }

    private static void ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Lease timestamps must use UTC.", nameof(value));
        }
    }

    private static void ValidateWorkerId(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        if (workerId.Length > 255)
        {
            throw new ArgumentException("Worker ID cannot exceed 255 characters.", nameof(workerId));
        }
    }
}
