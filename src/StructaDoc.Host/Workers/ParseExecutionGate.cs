using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Workers;

/// <summary>
/// Whether this Host currently sends documents to a Provider. Turning parsing on and off is routine
/// rather than a deployment change, so the execution worker asks this on every cycle instead of
/// reading the flag once at startup and requiring a restart to change its mind.
/// </summary>
public sealed class ParseExecutionGate(bool initiallyEnabled) : ISettingChangeListener
{
    private volatile bool isOpen = initiallyEnabled;

    public bool IsOpen => isOpen;

    public Task<bool> TryApplyAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(key, SettingCatalog.ParseExecutionEnabled, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        isOpen = bool.TryParse(value, out var enabled) && enabled;
        return Task.FromResult(true);
    }
}
