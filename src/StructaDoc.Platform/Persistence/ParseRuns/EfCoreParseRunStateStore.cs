using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Platform.Persistence.Entities;

namespace StructaDoc.Platform.Persistence.ParseRuns;

public sealed class EfCoreParseRunStateStore(StructaDocDbContext dbContext)
    : IParseRunStateStore
{
    public async Task<ParseRunLease?> TryStartAsync(
        ParseRunLease currentLease,
        string initialStage,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(currentLease, nowUtc);

        if (!ParseRunStages.IsKnown(initialStage))
        {
            throw new ArgumentException("The initial parse stage is not recognized.", nameof(initialStage));
        }

        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Claimed
                && (parseRun.ExternalTaskId == null || parseRun.Stage != null)
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.Running)
                    .SetProperty(
                        parseRun => parseRun.Stage,
                        parseRun => parseRun.ExternalTaskId == null
                            ? initialStage
                            : parseRun.Stage)
                    .SetProperty(
                        parseRun => parseRun.StartedAtUtc,
                        parseRun => parseRun.StartedAtUtc ?? nowUtc)
                    .SetProperty(parseRun => parseRun.ErrorCode, (string?)null)
                    .SetProperty(parseRun => parseRun.ErrorMessage, (string?)null)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken);

        return affectedRows == 1
            ? currentLease with
            {
                ConcurrencyVersion = currentLease.ConcurrencyVersion + 1,
            }
            : null;
    }

    public Task<ParseRunLease?> TryUpdateStageAsync(
        ParseRunLease currentLease,
        string stage,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(currentLease, nowUtc);
        ValidateStage(stage);

        return UpdateStageAsync(
            currentLease,
            stage,
            RequiresExternalTask(stage),
            nowUtc,
            cancellationToken);
    }

    public async Task<ParseRunLease?> TryRecordProviderSubmissionAsync(
        ParseRunLease currentLease,
        string externalTaskId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(currentLease, nowUtc);
        ValidateExternalTaskId(externalTaskId);

        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Running
                && parseRun.Stage == ParseRunStages.Submitting
                && parseRun.ExternalTaskId == null
                && parseRun.ProtectedSubmissionContinuation == null
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(parseRun => parseRun.ExternalTaskId, externalTaskId)
                    .SetProperty(parseRun => parseRun.Stage, ParseRunStages.WaitingProvider)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken);

        return affectedRows == 1
            ? currentLease with
            {
                ConcurrencyVersion = currentLease.ConcurrencyVersion + 1,
            }
            : null;
    }

    public async Task<ParseRunFailureTransition?> TryRecordFailureAsync(
        ParseRunLease currentLease,
        string errorCode,
        string? errorMessage,
        bool retryable,
        DateTime nextAttemptAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(currentLease, nowUtc);
        ValidateUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        ValidateError(errorCode, errorMessage);

        if (retryable && nextAttemptAtUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAttemptAtUtc),
                "A retry must be scheduled after the failure time.");
        }

        var candidate = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Running
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .Select(parseRun => new
            {
                parseRun.AttemptCount,
                parseRun.MaxAttempts,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return null;
        }

        var willRetry = retryable && candidate.AttemptCount < candidate.MaxAttempts;
        var nextStatus = willRetry
            ? ParseRunStatuses.RetryWait
            : ParseRunStatuses.Failed;

        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Running
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(parseRun => parseRun.Status, nextStatus)
                    .SetProperty(parseRun => parseRun.ErrorCode, errorCode)
                    .SetProperty(parseRun => parseRun.ErrorMessage, errorMessage)
                    .SetProperty(
                        parseRun => parseRun.ProtectedSubmissionContinuation,
                        parseRun => willRetry
                            ? parseRun.ProtectedSubmissionContinuation
                            : null)
                    .SetProperty(parseRun => parseRun.ClaimedBy, (string?)null)
                    .SetProperty(parseRun => parseRun.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(
                        parseRun => parseRun.NextAttemptAtUtc,
                        willRetry ? nextAttemptAtUtc : nowUtc)
                    .SetProperty(
                        parseRun => parseRun.CompletedAtUtc,
                        willRetry ? null : nowUtc)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken);

        return affectedRows == 1
            ? new ParseRunFailureTransition(
                currentLease.ParseRunId,
                nextStatus,
                currentLease.ConcurrencyVersion + 1)
            : null;
    }

    public async Task<int> QueueDueRetriesAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        var candidates = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun =>
                parseRun.Status == ParseRunStatuses.RetryWait
                && parseRun.NextAttemptAtUtc <= nowUtc)
            .OrderBy(parseRun => parseRun.NextAttemptAtUtc)
            .ThenBy(parseRun => parseRun.Id)
            .Select(parseRun => new
            {
                parseRun.Id,
                parseRun.ConcurrencyVersion,
            })
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        var queuedCount = 0;

        foreach (var candidate in candidates)
        {
            queuedCount += await dbContext.ParseRuns
                .Where(parseRun =>
                    parseRun.Id == candidate.Id
                    && parseRun.Status == ParseRunStatuses.RetryWait
                    && parseRun.NextAttemptAtUtc <= nowUtc
                    && parseRun.ConcurrencyVersion == candidate.ConcurrencyVersion)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.Queued)
                        .SetProperty(
                            parseRun => parseRun.ConcurrencyVersion,
                            parseRun => parseRun.ConcurrencyVersion + 1),
                    cancellationToken);
        }

        return queuedCount;
    }

    public async Task<int> FinalizeAbandonedCancellationsAsync(
        DateTime nowUtc,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        var candidates = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun =>
                parseRun.Status == ParseRunStatuses.CancelRequested
                && (parseRun.LeaseExpiresAtUtc == null || parseRun.LeaseExpiresAtUtc <= nowUtc))
            .OrderBy(parseRun => parseRun.CreatedAtUtc)
            .ThenBy(parseRun => parseRun.Id)
            .Select(parseRun => new
            {
                parseRun.Id,
                parseRun.ConcurrencyVersion,
            })
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        var cancelledCount = 0;

        foreach (var candidate in candidates)
        {
            cancelledCount += await dbContext.ParseRuns
                .Where(parseRun =>
                    parseRun.Id == candidate.Id
                    && parseRun.Status == ParseRunStatuses.CancelRequested
                    && (parseRun.LeaseExpiresAtUtc == null || parseRun.LeaseExpiresAtUtc <= nowUtc)
                    && parseRun.ConcurrencyVersion == candidate.ConcurrencyVersion)
                .ExecuteUpdateAsync(CancellationSetters(nowUtc), cancellationToken);
        }

        return cancelledCount;
    }

    public async Task<bool> TryFinalizeOwnedCancellationAsync(
        Guid parseRunId,
        string workerId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        // The cancellation request advanced the concurrency version, so the Worker's cached version
        // is stale by design. Claim ownership plus the requested status are the guard instead.
        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == parseRunId
                && parseRun.Status == ParseRunStatuses.CancelRequested
                && parseRun.ClaimedBy == workerId)
            .ExecuteUpdateAsync(CancellationSetters(nowUtc), cancellationToken);

        return affectedRows == 1;
    }

    private static Action<UpdateSettersBuilder<ParseRunEntity>> CancellationSetters(DateTime nowUtc) =>
        setters => setters
            .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.Cancelled)
            .SetProperty(parseRun => parseRun.Stage, (string?)null)
            .SetProperty(parseRun => parseRun.ClaimedBy, (string?)null)
            .SetProperty(parseRun => parseRun.LeaseExpiresAtUtc, (DateTime?)null)
            .SetProperty(parseRun => parseRun.ProtectedSubmissionContinuation, (string?)null)
            .SetProperty(parseRun => parseRun.CompletedAtUtc, nowUtc)
            .SetProperty(
                parseRun => parseRun.ConcurrencyVersion,
                parseRun => parseRun.ConcurrencyVersion + 1);

    private static void ValidateLease(ParseRunLease currentLease, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ValidateUtc(nowUtc, nameof(nowUtc));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLease.WorkerId);
    }

    private async Task<ParseRunLease?> UpdateStageAsync(
        ParseRunLease currentLease,
        string stage,
        bool requiresExternalTask,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Running
                && (requiresExternalTask
                    ? parseRun.ExternalTaskId != null
                    : parseRun.ExternalTaskId == null)
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(parseRun => parseRun.Stage, stage)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken);

        return affectedRows == 1
            ? currentLease with
            {
                ConcurrencyVersion = currentLease.ConcurrencyVersion + 1,
            }
            : null;
    }

    private static bool RequiresExternalTask(string stage)
    {
        return stage is ParseRunStages.WaitingProvider
            or ParseRunStages.Downloading
            or ParseRunStages.Normalizing
            or ParseRunStages.Persisting
            or ParseRunStages.CleaningUp;
    }

    private static void ValidateStage(string stage)
    {
        if (!ParseRunStages.IsKnown(stage))
        {
            throw new ArgumentException("The parse stage is not recognized.", nameof(stage));
        }
    }

    private static void ValidateExternalTaskId(string externalTaskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTaskId);

        if (externalTaskId.Length > 512)
        {
            throw new ArgumentException(
                "External task ID cannot exceed 512 characters.",
                nameof(externalTaskId));
        }

        if (!string.Equals(externalTaskId, externalTaskId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "External task ID cannot have leading or trailing whitespace.",
                nameof(externalTaskId));
        }

        if (externalTaskId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "External task ID cannot contain control characters.",
                nameof(externalTaskId));
        }
    }

    private static void ValidateUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Parse Run timestamps must use UTC.", parameterName);
        }
    }

    private static void ValidateError(string errorCode, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        if (errorCode.Length > 128)
        {
            throw new ArgumentException("Error code cannot exceed 128 characters.", nameof(errorCode));
        }

        if (errorMessage?.Length > 2048)
        {
            throw new ArgumentException(
                "Error message cannot exceed 2048 characters.",
                nameof(errorMessage));
        }
    }
}
