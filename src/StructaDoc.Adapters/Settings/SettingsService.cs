using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ServiceMantle.Audit;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.ControlPlane.Entities;
using StructaDoc.Application.Settings;

namespace StructaDoc.Adapters.Settings;

public sealed class SettingsService(
    ControlPlaneDbContext dbContext,
    StructaDocSettingsConfiguration configuration,
    ISettingSecretProtector secretProtector,
    StructaDocManagementAuditRecorder auditRecorder,
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
        var row = isManagedExternally ? null : stored.GetValueOrDefault(definition.Key);

        // A secret is stored encrypted, so what gets compared with the running value is its
        // plaintext. A row that cannot be decrypted is still a row: it is reported as set so an
        // administrator can write over it, but it is not in force, because the running service
        // dropped it for the same reason.
        var isSecret = SettingCatalog.IsSecret(definition);
        var chosen = isSecret && row is not null ? secretProtector.TryUnprotect(row) : row;
        var inForce = chosen ?? basis;

        return new SettingState(
            definition.Key,
            definition.Kind,
            // Only whether a secret is set is reported. The value never reaches a browser, so an
            // administration session that is read cannot give up a credential it did not write.
            isSecret ? string.Empty : inForce,
            definition.RequiresRestart,
            isManagedExternally,
            IsStored: row is not null,
            IsPendingRestart: definition.RequiresRestart
                && !string.Equals(inForce, running, StringComparison.Ordinal),
            definition.Minimum,
            definition.Maximum,
            definition.AllowedValues ?? []);
    }

    private static string Normalize(SettingDefinition definition, IConfiguration source)
    {
        return SettingCatalog.Normalize(definition, source[definition.Key]) ?? definition.Default;
    }

    public async Task<SettingWriteResult> SetAsync(
        string key,
        string? value,
        SettingActor actor,
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
            await RecordRejectedWriteAsync(
                actor,
                key,
                ManagementAuditOutcome.Failure,
                "The write named a key this service does not publish.",
                nowUtc,
                cancellationToken);
            return new SettingWriteResult(SettingWriteStatus.UnknownKey);
        }

        // Writing a value the deployment already pins would store something the service never uses,
        // which reads as a change that did not happen.
        if (configuration.IsManagedExternally(definition.Key))
        {
            await RecordRejectedWriteAsync(
                actor,
                key,
                ManagementAuditOutcome.Denied,
                "The key is pinned by the deployment, so a browser write cannot change it.",
                nowUtc,
                cancellationToken);
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
                // The audit row joins the row removal's save: one unit of work, so a setting is
                // never cleared without its audit trail, and an audit failure fails the clear.
                await StageWriteAuditAsync(
                    actor,
                    definition.Key,
                    ManagementAuditOutcome.Success,
                    "A stored setting was cleared, restoring its default.",
                    nowUtc,
                    cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            // Clearing restores the default, so listeners are told the default rather than the
            // value that was just removed.
            return await ApplyAsync(definition, definition.Default, cancellationToken);
        }

        var normalized = SettingCatalog.Normalize(definition, value);
        if (normalized is null)
        {
            await RecordRejectedWriteAsync(
                actor,
                key,
                ManagementAuditOutcome.Failure,
                "The submitted value is not one the key accepts.",
                nowUtc,
                cancellationToken);
            return new SettingWriteResult(SettingWriteStatus.InvalidValue);
        }

        // What goes in the row is not always what the service reads. A secret is encrypted here so
        // that a copy of the database file, which travels with every backup, does not carry it.
        var persisted = SettingCatalog.IsSecret(definition)
            ? secretProtector.Protect(normalized)
            : normalized;

        if (existing is null)
        {
            dbContext.Settings.Add(new SettingEntity
            {
                Key = definition.Key,
                Value = persisted,
                UpdatedAtUtc = nowUtc,
                UpdatedBy = actor.Username,
            });
        }
        else
        {
            existing.Value = persisted;
            existing.UpdatedAtUtc = nowUtc;
            existing.UpdatedBy = actor.Username;
        }

        // The audit row joins the setting row's save. The value never enters the audit event: the
        // key name is the target, and the outcome is the result.
        await StageWriteAuditAsync(
            actor,
            definition.Key,
            ManagementAuditOutcome.Success,
            "A stored setting was written.",
            nowUtc,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        return await ApplyAsync(definition, normalized, cancellationToken);
    }

    /// <summary>
    /// Stages the settings record point on the same control-plane context the write uses, so the
    /// audit row and the setting row commit and roll back together. A rejected construction fails
    /// the write rather than letting a setting change land without its audit trail.
    /// </summary>
    private async ValueTask StageWriteAuditAsync(
        SettingActor actor,
        string key,
        ManagementAuditOutcome outcome,
        string? securityDescription,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await auditRecorder.StageAsync(
            CreateWriteAuditEvent(actor, key, outcome, securityDescription, nowUtc),
            cancellationToken);
    }

    /// <summary>
    /// Records a write that was rejected before anything was staged, as its own audit-only row.
    /// The actor, the key name, and the rejection are the whole record.
    /// </summary>
    private async Task RecordRejectedWriteAsync(
        SettingActor actor,
        string key,
        ManagementAuditOutcome outcome,
        string? securityDescription,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var auditEvent = CreateWriteAuditEvent(actor, key, outcome, securityDescription, nowUtc);
        await auditRecorder.RecordAsync(auditEvent, cancellationToken);
    }

    private static ManagementAuditEvent CreateWriteAuditEvent(
        SettingActor actor,
        string key,
        ManagementAuditOutcome outcome,
        string? securityDescription,
        DateTime nowUtc)
    {
        return ManagementAuditEvent.Create(
            StructaDocManagementAudit.AdministratorOperator(
                actor.AdministratorId,
                actor.Username),
            WellKnownManagementAuditActions.ConfigurationChanged,
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, key),
            outcome,
            occurredAtUtc: new DateTimeOffset(nowUtc),
            securityDescription: securityDescription);
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
