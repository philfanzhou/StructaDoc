namespace StructaDoc.Contracts.Providers;

/// <summary>
/// One optional Provider setting as the administration page needs to present it: whether this type
/// reads it, and what is sent when it is left blank.
/// </summary>
public sealed record ProviderSettingResponse(bool IsUsed, string? AppliedDefault);

/// <summary>
/// What a Provider type needs before it can be configured. The service answers this rather than the
/// browser holding its own copy, so a default shown beside a field is the one the outbound request
/// carries.
/// </summary>
public sealed record ProviderTypeResponse(
    string ProviderType,
    string? SuggestedBaseUrl,
    ProviderSettingResponse Model,
    ProviderSettingResponse Backend);
