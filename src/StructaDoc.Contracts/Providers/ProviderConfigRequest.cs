namespace StructaDoc.Contracts.Providers;

public sealed record ProviderConfigRequest(
    string? Name,
    string? ProviderType,
    string? BaseUrl,
    string? Model = null,
    string? Backend = null,
    string? Credential = null,
    bool ClearCredential = false,
    bool IsEnabled = true,
    bool IsDefault = false);
