namespace StructaDoc.Contracts.Settings;

public sealed record SettingResponse(
    string Key,
    string Kind,
    string Value,
    bool RequiresRestart,
    bool IsManagedExternally,
    bool IsStored,
    bool IsPendingRestart,
    long Minimum,
    long Maximum);

/// <summary>
/// An empty <paramref name="Value"/> clears the stored setting and restores the shipped default.
/// </summary>
public sealed record SettingUpdateRequest(string Key, string? Value);

public sealed record SettingUpdateResponse(bool RestartRequired);

public sealed record RestartAcceptedResponse(string Detail);

/// <summary>
/// <paramref name="Enabled"/> is what the running service is doing, not what is stored, so a
/// configuration saved but not yet restarted into reads as disabled.
/// <paramref name="StartupFault"/> is set when a stored configuration was refused while starting;
/// without it a rejected value would appear on the page as though it were in effect.
///
/// <paramref name="CallbackPath"/> and <paramref name="Scopes"/> are reported rather than settable.
/// The redirect address has to be registered at the identity provider, and an administrator cannot
/// register what the service will not tell them.
/// </summary>
public sealed record OidcStatusResponse(
    bool Enabled,
    string? StartupFault,
    string CallbackPath,
    string SignedOutCallbackPath,
    IReadOnlyList<string> Scopes);

public sealed record OidcConnectionTestRequest(string? Authority, bool RequireHttpsMetadata);

/// <summary>
/// <paramref name="Code"/> is a stable token the web interface translates; <paramref name="Detail"/>
/// carries the part only this deployment knows, such as a status code or the issuer that answered.
/// </summary>
public sealed record OidcConnectionTestResponse(
    bool Succeeded,
    string Code,
    string Detail,
    string? Issuer);
