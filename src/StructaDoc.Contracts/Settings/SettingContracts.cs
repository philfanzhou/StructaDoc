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
