namespace StructaDoc.Application.ParseRuns;

public interface IParseSegmentMutationStore
{
    Task<ParseRunLease?> TryCreateAsync(
        ParseRunLease currentLease,
        IReadOnlyList<ParseSegmentCreation> segments,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<ParseRunLease?> TryUpdateCheckpointAsync(
        ParseRunLease currentLease,
        ParseSegmentCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed record ParseSegmentCreation(
    Guid Id,
    int Index,
    int StartPage,
    int EndPage,
    string StorageRef,
    long SizeBytes,
    string Sha256,
    string Status);

public sealed record ParseSegmentCheckpoint(
    Guid Id,
    string Status,
    string? ExternalTaskId,
    string? ProtectedSubmissionContinuation);
