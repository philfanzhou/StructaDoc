namespace StructaDoc.Application.Providers;

public sealed record ProviderConfigRecord(
    Guid Id,
    string Name,
    string ProviderType,
    bool IsEnabled,
    bool IsDefault,
    Guid CurrentVersionId,
    int VersionNumber,
    string BaseUrl,
    string? Model,
    string? Backend,
    bool HasCredential,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public enum ProviderConfigMutationStatus
{
    Succeeded,
    NotFound,
    Conflict,
}

public sealed record ProviderConfigMutationResult(
    ProviderConfigMutationStatus Status,
    ProviderConfigRecord? Config = null);
