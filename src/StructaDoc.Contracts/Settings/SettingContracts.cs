namespace StructaDoc.Contracts.Settings;

/// <summary>
/// <paramref name="AllowedValues"/> is empty for a free-text setting and lists the accepted spellings
/// for a closed one, so the web interface can offer a choice instead of asking someone to guess.
/// </summary>
public sealed record SettingResponse(
    string Key,
    string Kind,
    string Value,
    bool RequiresRestart,
    bool IsManagedExternally,
    bool IsStored,
    bool IsPendingRestart,
    long Minimum,
    long Maximum,
    IReadOnlyList<string> AllowedValues);

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

/// <summary>
/// What the running service is using for documents and results, which is not always what is stored:
/// a change takes effect at the next restart, and a stored section the service refused is reported
/// through <paramref name="StartupFault"/> rather than shown as if it were in force.
/// </summary>
public sealed record StorageStatusResponse(
    string Provider,
    string? StartupFault,
    bool HasCredential);

/// <summary>
/// What the running service is using as its business database. The connection string is never
/// included: it usually carries a password, and a read of the administration area must not be able
/// to give up a credential the reader did not write.
/// </summary>
public sealed record DatabaseStatusResponse(
    string Provider,
    string? StartupFault,
    bool IsReachable,
    bool HasPendingMigrations);

/// <summary>
/// A configuration to try before committing to it. Every field is optional and an omitted one falls
/// back to what is in force, so an administrator can test a single change, or test what is already
/// saved without having to retype a secret the service never sends back.
/// </summary>
public sealed record StorageConnectionTestRequest(
    string? Provider = null,
    string? RootPath = null,
    string? ServiceUrl = null,
    string? Region = null,
    string? Bucket = null,
    string? Prefix = null,
    string? AccessKey = null,
    string? SecretKey = null,
    bool? ForcePathStyle = null);

public sealed record DatabaseConnectionTestRequest(
    string? Provider = null,
    string? ConnectionString = null,
    string? ServerVersion = null);

/// <summary>
/// <paramref name="Code"/> is a stable token the web interface translates; <paramref name="Detail"/>
/// carries the part only this deployment knows, and never a credential it was given.
/// </summary>
public sealed record ConnectionTestResponse(bool Succeeded, string Code, string Detail);
