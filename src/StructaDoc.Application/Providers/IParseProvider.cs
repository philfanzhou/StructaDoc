namespace StructaDoc.Application.Providers;

public interface IParseProvider
{
    string ProviderType { get; }

    Task<ProviderCapabilities> GetCapabilitiesAsync(
        ProviderExecutionConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<ProviderSubmissionCheckpoint?> PrepareSubmissionAsync(
        ProviderExecutionConfiguration configuration,
        Guid parseRunId,
        ProviderDocumentSource source,
        string optionsJson,
        CancellationToken cancellationToken = default);

    Task<ProviderSubmission> SubmitAsync(
        ProviderExecutionConfiguration configuration,
        Guid parseRunId,
        ProviderDocumentSource source,
        string optionsJson,
        ProviderSubmissionCheckpoint? checkpoint,
        CancellationToken cancellationToken = default);

    Task<ProviderTaskStatus> GetStatusAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default);

    Task<ProviderResultContent> OpenResultAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default);

    Task TryCancelAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default);
}
