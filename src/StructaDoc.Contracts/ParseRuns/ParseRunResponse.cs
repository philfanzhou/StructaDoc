using System.Text.Json;

namespace StructaDoc.Contracts.ParseRuns;

public sealed record ParseRunResponse(
    Guid Id,
    Guid DocumentId,
    string Status,
    string? Stage,
    string ProviderType,
    Guid ProviderConfigId,
    Guid ProviderConfigVersionId,
    JsonElement Options,
    string SourceMediaType,
    string SubmittedMediaType,
    int AttemptCount,
    int MaxAttempts,
    DateTime NextAttemptAt,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);
