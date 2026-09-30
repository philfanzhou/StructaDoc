namespace StructaDoc.Application.Settings;

/// <summary>
/// <paramref name="Value"/> is the value that applies, from whichever source won.
/// <paramref name="IsManagedExternally"/> says an administrator cannot change it here because the
/// deployment already set it, and <paramref name="IsStored"/> says the value is one an administrator
/// chose rather than the shipped default. Writing an empty value clears the stored row, which
/// restores the default rather than setting the value to nothing.
///
/// <paramref name="IsPendingRestart"/> separates what was chosen from what is running: options were
/// bound at startup, so a stored value the running service has not picked up must say so rather than
/// be reported as if it had taken effect.
/// </summary>
public sealed record SettingState(
    string Key,
    SettingKind Kind,
    string Value,
    bool RequiresRestart,
    bool IsManagedExternally,
    bool IsStored,
    bool IsPendingRestart,
    long Minimum,
    long Maximum,
    IReadOnlyList<string> AllowedValues);

public enum SettingWriteStatus
{
    Succeeded,
    UnknownKey,
    InvalidValue,
    ManagedExternally,
}

public sealed record SettingWriteResult(SettingWriteStatus Status, bool RestartRequired = false);

/// <summary>
/// The administrator a setting write is attributed to: the account's stable identifier for the
/// audit operator, and the username the settings row has always recorded.
/// </summary>
public sealed record SettingActor(string AdministratorId, string Username);

/// <summary>
/// Applies a changed setting to something already running. Returning <see langword="false"/> means
/// this listener does not handle the key, which is how a setting is known to need a restart.
/// </summary>
public interface ISettingChangeListener
{
    Task<bool> TryApplyAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    Task<IReadOnlyList<SettingState>> ListAsync(CancellationToken cancellationToken = default);

    Task<SettingWriteResult> SetAsync(
        string key,
        string? value,
        SettingActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
