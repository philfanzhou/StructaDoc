using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ParseRuns;

public interface IRunExecutionContextStore
{
    Task<RunExecutionContext?> LoadAsync(
        RunLease currentLease,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed record RunExecutionContext(
    Guid ParseRunId,
    Guid DocumentId,
    string OriginalFileName,
    string SourceMediaType,
    string SubmittedMediaType,
    long SourceSizeBytes,
    string SourceSha256,
    string SourceStorageRef,
    string OptionsJson,
    string Stage,
    RunConversion? Conversion,
    string? ExternalTaskId,
    ProviderSubmissionCheckpoint? SubmissionCheckpoint,
    int AttemptCount,
    ProviderExecutionConfiguration ProviderConfiguration)
{
    public override string ToString() =>
        $"RunExecutionContext {{ ParseRunId = {ParseRunId}, DocumentId = {DocumentId}, ProviderType = {ProviderConfiguration.ProviderType} }}";
}
