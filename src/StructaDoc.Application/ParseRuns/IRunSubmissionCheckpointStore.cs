using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ParseRuns;

public interface IRunSubmissionCheckpointStore
{
    Task<RunLease?> TrySaveAsync(
        RunLease currentLease,
        ProviderSubmissionCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<RunLease?> TryCompleteAsync(
        RunLease currentLease,
        ProviderSubmissionCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
