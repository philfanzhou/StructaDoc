using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Adapters.Persistence.ParseRuns;

public sealed class EfCoreParseSegmentMutationStore(StructaDocDbContext dbContext)
    : IParseSegmentMutationStore
{
    public async Task<ParseRunLease?> TryCreateAsync(
        ParseRunLease currentLease,
        IReadOnlyList<ParseSegmentCreation> segments,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(currentLease, nowUtc);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one Parse Segment is required.", nameof(segments));
        }

        foreach (var segment in segments)
        {
            ValidateCreation(segment);
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var updatedLease = await TryFenceLeaseAsync(currentLease, nowUtc, cancellationToken);
            if (updatedLease is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            dbContext.ParseSegments.AddRange(segments.Select(segment => new ParseSegmentEntity
            {
                Id = segment.Id,
                ParseRunId = currentLease.ParseRunId,
                Index = segment.Index,
                StartPage = segment.StartPage,
                EndPage = segment.EndPage,
                StorageRef = segment.StorageRef,
                SizeBytes = segment.SizeBytes,
                Sha256 = segment.Sha256,
                Status = segment.Status,
                UpdatedAtUtc = nowUtc,
            }));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updatedLease;
        });
    }

    public async Task<ParseRunLease?> TryUpdateCheckpointAsync(
        ParseRunLease currentLease,
        ParseSegmentCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(currentLease, nowUtc);
        ValidateCheckpoint(checkpoint);

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var updatedLease = await TryFenceLeaseAsync(currentLease, nowUtc, cancellationToken);
            if (updatedLease is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var affectedSegments = await dbContext.ParseSegments
                .Where(segment =>
                    segment.Id == checkpoint.Id
                    && segment.ParseRunId == currentLease.ParseRunId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(segment => segment.Status, checkpoint.Status)
                        .SetProperty(segment => segment.ExternalTaskId, checkpoint.ExternalTaskId)
                        .SetProperty(
                            segment => segment.ProtectedSubmissionContinuation,
                            checkpoint.ProtectedSubmissionContinuation)
                        .SetProperty(segment => segment.UpdatedAtUtc, nowUtc),
                    cancellationToken);
            if (affectedSegments != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "The fenced Parse Segment checkpoint target does not exist.");
            }

            await transaction.CommitAsync(cancellationToken);
            return updatedLease;
        });
    }

    private async Task<ParseRunLease?> TryFenceLeaseAsync(
        ParseRunLease currentLease,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var affectedRuns = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Running
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    parseRun => parseRun.ConcurrencyVersion,
                    parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken);

        return affectedRuns == 1
            ? currentLease with
            {
                ConcurrencyVersion = currentLease.ConcurrencyVersion + 1,
            }
            : null;
    }

    private static void ValidateLease(ParseRunLease currentLease, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLease.WorkerId);
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Parse Segment timestamps must use UTC.", nameof(nowUtc));
        }
    }

    private static void ValidateCreation(ParseSegmentCreation segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Id == Guid.Empty)
        {
            throw new ArgumentException("Parse Segment IDs cannot be empty.", nameof(segment));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(segment.Index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segment.StartPage);
        if (segment.EndPage < segment.StartPage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segment),
                "A Parse Segment end page cannot precede its start page.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.StorageRef);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segment.SizeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.Sha256);
        if (segment.Sha256.Length != 64)
        {
            throw new ArgumentException("A Parse Segment SHA-256 must contain 64 characters.", nameof(segment));
        }
        ValidateStatus(segment.Status);
    }

    private static void ValidateCheckpoint(ParseSegmentCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Id == Guid.Empty)
        {
            throw new ArgumentException("Parse Segment IDs cannot be empty.", nameof(checkpoint));
        }
        ValidateStatus(checkpoint.Status);
        if (checkpoint.ExternalTaskId?.Length > 512)
        {
            throw new ArgumentException(
                "A Parse Segment external task ID cannot exceed 512 characters.",
                nameof(checkpoint));
        }
    }

    private static void ValidateStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (status.Length > 32)
        {
            throw new ArgumentException("A Parse Segment status cannot exceed 32 characters.", nameof(status));
        }
    }
}
