using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ParseRuns;

public interface IParseRunSubmissionCheckpointStore
{
    Task<ParseRunLease?> TrySaveAsync(
        ParseRunLease currentLease,
        ProviderSubmissionCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<ParseRunLease?> TryCompleteAsync(
        ParseRunLease currentLease,
        ProviderSubmissionCheckpoint checkpoint,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
