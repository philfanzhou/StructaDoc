using System.Globalization;
using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Workers;

/// <summary>
/// Applies <see cref="SettingCatalog.ParseMaxConcurrency"/> to the running execution gate, which
/// is the whole difference between the setting taking effect now and taking effect at the next
/// start. A change the gate cannot use returns <see langword="false"/>, so the write is answered
/// with the restart it still needs rather than a silent no-op — the failure mode the removed
/// Worker execution switch was deleted for.
/// </summary>
public sealed class WorkerConcurrencySettingListener(
    WorkerConcurrencyGate gate,
    ILogger<WorkerConcurrencySettingListener> logger) : ISettingChangeListener
{
    public Task<bool> TryApplyAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(key, SettingCatalog.ParseMaxConcurrency, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrency)
            || !gate.TrySetLimit(concurrency))
        {
            return Task.FromResult(false);
        }

        logger.LogInformation(
            "Parse Run execution concurrency changed to {MaxConcurrency}; executions already running keep their slots.",
            concurrency);
        return Task.FromResult(true);
    }

    public bool TryGetAppliedValue(string key, out string? appliedValue)
    {
        if (!string.Equals(key, SettingCatalog.ParseMaxConcurrency, StringComparison.Ordinal))
        {
            appliedValue = null;
            return false;
        }

        appliedValue = gate.Limit.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
