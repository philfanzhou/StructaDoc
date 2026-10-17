using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Logging;

/// <summary>
/// Applies the three <c>Logging:LogLevel:*</c> settings to the running console pipeline's level
/// switches. A change that names a level the pipeline accepts flips the switch and takes effect on
/// the events written from that point on, with no restart. A key pinned by the deployment never
/// reaches this listener — the settings write refuses it first — and a value the pipeline cannot
/// use returns <see langword="false"/>, so the write is answered with the restart it still needs
/// rather than a silent no-op.
/// </summary>
public sealed class LoggingLevelSettingListener(
    StructaDocConsoleLogSwitches switches,
    ILogger<LoggingLevelSettingListener> logger) : ISettingChangeListener
{
    public Task<bool> TryApplyAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (!switches.TrySet(key, value, out var applied))
        {
            return Task.FromResult(false);
        }

        // Information on purpose: the event itself is what proves the change took effect, because
        // it is written through the pipeline the switch now governs. Lowering the root level to
        // Warning or above suppresses this notice for the next change, which is the filtering the
        // administrator asked for rather than a failure to report one.
        logger.LogInformation(
            "The console log level for '{SettingKey}' changed to '{LogLevel}'; events written from this point on follow it.",
            key,
            applied);
        return Task.FromResult(true);
    }

    public bool TryGetAppliedValue(string key, out string? appliedValue)
    {
        return switches.TryGet(key, out appliedValue);
    }
}
