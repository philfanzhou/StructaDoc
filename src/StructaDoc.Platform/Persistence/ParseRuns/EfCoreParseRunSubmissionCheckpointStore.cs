using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Platform.Persistence.ParseRuns;

public sealed class EfCoreParseRunSubmissionCheckpointStore(
    StructaDocDbContext dbContext,
    IProviderSubmissionProtector submissionProtector) : IParseRunSubmissionCheckpointStore
{
    public async Task<ParseRunLease?> TrySaveAsync(
        ParseRunLease currentLease,
        ProviderSubmissionCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        Validate(currentLease, checkpoint, nowUtc);
        var protectedContinuation = submissionProtector.Protect(checkpoint.ContinuationToken);

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
                    .SetProperty(parseRun => parseRun.ExternalTaskId, checkpoint.ExternalTaskId)
                    .SetProperty(
                        parseRun => parseRun.ProtectedSubmissionContinuation,
                        protectedContinuation)
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

    public async Task<ParseRunLease?> TryCompleteAsync(
        ParseRunLease currentLease,
        ProviderSubmissionCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        Validate(currentLease, checkpoint, nowUtc);

        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Running
                && parseRun.Stage == ParseRunStages.Submitting
                && parseRun.ExternalTaskId == checkpoint.ExternalTaskId
                && parseRun.ProtectedSubmissionContinuation != null
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        parseRun => parseRun.ProtectedSubmissionContinuation,
                        (string?)null)
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

    private static void Validate(
        ParseRunLease currentLease,
        ProviderSubmissionCheckpoint checkpoint,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Checkpoint timestamps must use UTC.", nameof(nowUtc));
        }
    }
}
