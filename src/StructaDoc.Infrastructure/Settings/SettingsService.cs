using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StructaDoc.Application.Settings;
using StructaDoc.Infrastructure.ControlPlane;
using StructaDoc.Infrastructure.ControlPlane.Entities;

namespace StructaDoc.Infrastructure.Settings;

public sealed class SettingsService(
    ControlPlaneDbContext dbContext,
    StructaDocSettingsConfiguration configuration,
    IEnumerable<ISettingChangeListener> listeners) : ISettingsService
{
    public async Task<IReadOnlyList<SettingState>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await dbContext.Settings
            .AsNoTracking()
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        return SettingCatalog.All.Select(definition => ToState(definition, stored)).ToArray();
    }

    private SettingState ToState(
        SettingDefinition definition,
        IReadOnlyDictionary<string, string> stored)
    {
        // Values are normalized because the same boolean arrives as "False" from a JSON file and
        // "false" from the store, and a caller comparing the two spellings would read one wrongly.
        // What this process bound its options from. A value written since then differs from it.
        var running = Normalize(definition, configuration.Effective);

        // What applies with no stored row, which is not the same as what is running: a row deleted
        // since startup is gone from here but still present in what the process is using.
        var basis = Normalize(definition, configuration.Base);

        // A row left over from before the deployment pinned the key is dead weight, not the value in
        // force, so it must not be reported as one.
        var isManagedExternally = configuration.IsManagedExternally(definition.Key);
        var chosen = isManagedExternally ? null : stored.GetValueOrDefault(definition.Key);
        var inForce = chosen ?? basis;

        return new SettingState(
            definition.Key,
            definition.Kind,
            inForce,
            definition.RequiresRestart,
            isManagedExternally,
            IsStored: chosen is not null,
            IsPendingRestart: definition.RequiresRestart
                && !string.Equals(inForce, running, StringComparison.Ordinal),
            definition.Minimum,
            definition.Maximum);
    }

    private static string Normalize(SettingDefinition definition, IConfiguration source)
    {
        return SettingCatalog.Normalize(definition, source[definition.Key]) ?? definition.Default;
    }

    public async Task<SettingWriteResult> SetAsync(
        string key,
        string? value,
        string updatedBy,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Setting timestamps must use UTC.", nameof(nowUtc));
        }

        var definition = SettingCatalog.Find(key);
        if (definition is null)
        {
            return new SettingWriteResult(SettingWriteStatus.UnknownKey);
        }

        // Writing a value the deployment already pins would store something the service never uses,
        // which reads as a change that did not happen.
        if (configuration.IsManagedExternally(definition.Key))
        {
            return new SettingWriteResult(SettingWriteStatus.ManagedExternally);
        }

        var existing = await dbContext.Settings.SingleOrDefaultAsync(
            setting => setting.Key == definition.Key,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (existing is not null)
            {
                dbContext.Settings.Remove(existing);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

                // Clearing restores the default, so listeners are told the default rather than the
                // value that was just removed.
                return await ApplyAsync(definition, definition.Default, cancellationToken);
        }

        var normalized = SettingCatalog.Normalize(definition, value);
        if (normalized is null)
        {
            return new SettingWriteResult(SettingWriteStatus.InvalidValue);
        }

        if (existing is null)
        {
            dbContext.Settings.Add(new SettingEntity
            {
                Key = definition.Key,
                Value = normalized,
                UpdatedAtUtc = nowUtc,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            existing.Value = normalized;
            existing.UpdatedAtUtc = nowUtc;
            existing.UpdatedBy = updatedBy;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await ApplyAsync(definition, normalized, cancellationToken);
    }

    /// <summary>
    /// Configuration was read into options at startup, so a stored value only reaches the running
    /// service through a listener. A setting with no listener needs a restart, and says so rather
    /// than reporting a change that has not taken effect.
    /// </summary>
    private async Task<SettingWriteResult> ApplyAsync(
        SettingDefinition definition,
        string? effectiveValue,
        CancellationToken cancellationToken)
    {
        var applied = false;

        foreach (var listener in listeners)
        {
            if (await listener.TryApplyAsync(definition.Key, effectiveValue, cancellationToken))
            {
                applied = true;
            }
        }

        // Reported from what actually happened rather than from the catalog flag, so a setting that
        // lost its listener says a restart is needed instead of claiming an effect it did not have.
        return new SettingWriteResult(SettingWriteStatus.Succeeded, RestartRequired: !applied);
    }
}
