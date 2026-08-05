using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ParseRuns;

public interface IParseRunExecutionContextStore
{
    Task<ParseRunExecutionContext?> LoadAsync(
        ParseRunLease currentLease,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed record ParseRunExecutionContext(
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
    ParseRunConversion? Conversion,
    string? ExternalTaskId,
    ProviderSubmissionCheckpoint? SubmissionCheckpoint,
    int AttemptCount,
    ProviderExecutionConfiguration ProviderConfiguration)
{
    public override string ToString() =>
        $"ParseRunExecutionContext {{ ParseRunId = {ParseRunId}, DocumentId = {DocumentId}, ProviderType = {ProviderConfiguration.ProviderType} }}";
}
