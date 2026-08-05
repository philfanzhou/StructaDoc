namespace StructaDoc.Contracts.Providers;

public sealed record ProviderConfigResponse(
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
    DateTime CreatedAt,
    DateTime UpdatedAt);
