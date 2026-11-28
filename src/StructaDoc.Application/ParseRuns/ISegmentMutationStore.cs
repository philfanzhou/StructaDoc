namespace StructaDoc.Application.ParseRuns;

public interface ISegmentMutationStore
{
    Task<RunLease?> TryCreateAsync(
        RunLease currentLease,
        IReadOnlyList<SegmentCreation> segments,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<RunLease?> TryUpdateCheckpointAsync(
        RunLease currentLease,
        SegmentCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed record SegmentCreation(
    Guid Id,
    int Index,
    int StartPage,
    int EndPage,
    string StorageRef,
    long SizeBytes,
    string Sha256,
    string Status);

public sealed record SegmentCheckpoint(
    Guid Id,
    string Status,
    string? ExternalTaskId,
    string? ProtectedSubmissionContinuation);
