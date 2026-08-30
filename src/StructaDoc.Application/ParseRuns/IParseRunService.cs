using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.ParseRuns;

public interface IParseRunService
{
    Task<ParseRunCreationResult> CreateAsync(
        ParseRunCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<ParseRunRecord?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<ParseRunCancellationResult> RequestCancellationAsync(
        Guid id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed record ParseRunCreateRequest(
    Guid DocumentId,
    Guid? ProviderConfigId,
    string OptionsJson,
    int MaxAttempts,
    CanonicalActor Actor,
    string? IdempotencyKey,
    DateTime CreatedAtUtc);

public sealed record ParseRunRecord(
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

public enum ParseRunCreationStatus
{
    Created,
    Replayed,
    DocumentNotFound,
    ProviderConfigNotFound,
    ProviderUnavailable,
    ProviderCredentialMissing,
    Conflict,
}

public sealed record ParseRunCreationResult(
    ParseRunCreationStatus Status,
    ParseRunRecord? ParseRun = null);

public enum ParseRunCancellationStatus
{
    Requested,
    AlreadyRequested,
    AlreadyFinal,
    NotFound,
    Conflict,
}

public sealed record ParseRunCancellationResult(
    ParseRunCancellationStatus Status,
    ParseRunRecord? ParseRun = null);
