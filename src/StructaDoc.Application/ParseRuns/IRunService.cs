using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.ParseRuns;

public interface IRunService
{
    Task<RunCreationResult> CreateAsync(
        RunCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<RunRecord?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<RunCancellationResult> RequestCancellationAsync(
        Guid id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed record RunCreateRequest(
    Guid DocumentId,
    Guid? ProviderConfigId,
    string OptionsJson,
    int MaxAttempts,
    CanonicalActor Actor,
    string? IdempotencyKey,
    DateTime CreatedAtUtc);

public sealed record RunRecord(
    Guid Id,
    Guid DocumentId,
    string Status,
    string? Stage,
    string ProviderType,
    Guid ProviderConfigId,
    Guid ProviderConfigVersionId,
    string OptionsJson,
    string SourceMediaType,
    string SubmittedMediaType,
    int AttemptCount,
    int MaxAttempts,
    DateTime NextAttemptAtUtc,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc);

public enum RunCreationStatus
{
    Created,
    Replayed,
    DocumentNotFound,
    ProviderConfigNotFound,
    ProviderUnavailable,
    ProviderCredentialMissing,
    Conflict,
}

public sealed record RunCreationResult(
    RunCreationStatus Status,
    RunRecord? ParseRun = null);

public enum RunCancellationStatus
{
    Requested,
    AlreadyRequested,
    AlreadyFinal,
    NotFound,
    Conflict,
}

public sealed record RunCancellationResult(
    RunCancellationStatus Status,
    RunRecord? ParseRun = null);
