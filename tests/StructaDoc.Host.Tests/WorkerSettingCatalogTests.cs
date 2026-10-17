using StructaDoc.Application.Settings;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The Worker section of the settings catalog restates defaults and bounds that live in
/// <see cref="ParseRunWorkerOptions"/>. These live here rather than beside the other catalog
/// contract tests because the options class is part of the Host, which those tests do not
/// reference; the assertions are the same shape and serve the same purpose.
/// </summary>
public sealed class WorkerSettingCatalogTests
{
    [Fact]
    public void Worker_defaults_match_the_options_they_describe()
    {
        var options = new ParseRunWorkerOptions();

        Assert.Equal(
            options.MaintenanceInterval.ToString(),
            Definition(SettingCatalog.WorkerMaintenanceInterval).Default);
        Assert.Equal(
            options.RecoveryBatchSize.ToString(),
            Definition(SettingCatalog.WorkerRecoveryBatchSize).Default);
        Assert.Equal(
            options.LeaseDuration.ToString(),
            Definition(SettingCatalog.WorkerLeaseDuration).Default);
        Assert.Equal(
            options.HeartbeatInterval.ToString(),
            Definition(SettingCatalog.WorkerHeartbeatInterval).Default);
        Assert.Equal(
            options.RetryDelay.ToString(),
            Definition(SettingCatalog.WorkerRetryDelay).Default);
        Assert.Equal(
            options.MinimumPollDelay.ToString(),
            Definition(SettingCatalog.WorkerMinimumPollDelay).Default);
        Assert.Equal(
            options.MaximumPollDelay.ToString(),
            Definition(SettingCatalog.WorkerMaximumPollDelay).Default);
        Assert.Equal(
            options.MaxExecutionDuration.ToString(),
            Definition(SettingCatalog.WorkerMaxExecutionDuration).Default);
    }

    /// <summary>
    /// The write-time bounds and the startup bounds are the same bound: the minimum itself has to
    /// validate, one step below it has to fail validation, and the duration keys state their
    /// floors in exactly the milliseconds the catalog bounds them in. Combinations that only fail
    /// together — a heartbeat no shorter than its lease, a poll ceiling below its floor — are the
    /// recoverable binder's subject, not the catalog's.
    /// </summary>
    [Fact]
    public void Worker_ranges_match_the_bounds_validation_enforces()
    {
        foreach (var key in new[]
        {
            SettingCatalog.WorkerMaintenanceInterval,
            SettingCatalog.WorkerHeartbeatInterval,
            SettingCatalog.WorkerMinimumPollDelay,
        })
        {
            var definition = Definition(key);
            Assert.Equal(SettingKind.Duration, definition.Kind);
            WorkerWhere(key, definition.Minimum).Validate();
            Assert.ThrowsAny<Exception>(() => WorkerWhere(key, definition.Minimum - 1).Validate());
        }

        // The lease and poll-ceiling floors sit beside rules that compare them with their
        // siblings, so their floor probes lower the sibling with them: a lease at its floor cannot
        // keep housing the default heartbeat, and a poll ceiling at its floor cannot stay above
        // the default poll floor.
        var lease = Definition(SettingCatalog.WorkerLeaseDuration);
        new ParseRunWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(
                Definition(SettingCatalog.WorkerHeartbeatInterval).Minimum),
            LeaseDuration = TimeSpan.FromMilliseconds(lease.Minimum),
        }.Validate();
        Assert.ThrowsAny<Exception>(() => new ParseRunWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(
                Definition(SettingCatalog.WorkerHeartbeatInterval).Minimum),
            LeaseDuration = TimeSpan.FromMilliseconds(lease.Minimum - 1),
        }.Validate());

        var maximumPoll = Definition(SettingCatalog.WorkerMaximumPollDelay);
        var minimumPollFloor = Definition(SettingCatalog.WorkerMinimumPollDelay).Minimum;
        Assert.Equal(minimumPollFloor, maximumPoll.Minimum);
        new ParseRunWorkerOptions
        {
            MinimumPollDelay = TimeSpan.FromMilliseconds(minimumPollFloor),
            MaximumPollDelay = TimeSpan.FromMilliseconds(maximumPoll.Minimum),
        }.Validate();
        Assert.ThrowsAny<Exception>(() => new ParseRunWorkerOptions
        {
            MinimumPollDelay = TimeSpan.FromMilliseconds(minimumPollFloor),
            MaximumPollDelay = TimeSpan.FromMilliseconds(maximumPoll.Minimum - 1),
        }.Validate());

        var recovery = Definition(SettingCatalog.WorkerRecoveryBatchSize);
        WorkerWhere(SettingCatalog.WorkerRecoveryBatchSize, recovery.Minimum).Validate();
        Assert.ThrowsAny<Exception>(
            () => WorkerWhere(SettingCatalog.WorkerRecoveryBatchSize, recovery.Minimum - 1).Validate());

        // Zero is the documented "no execution bound" value, so the duration floor sits at it. A
        // positive value below one second is refused by validation at startup, which is the
        // recovery path rather than a write-time bound, because no single interval expresses
        // "zero, or at least a second".
        var execution = Definition(SettingCatalog.WorkerMaxExecutionDuration);
        Assert.Equal(0, execution.Minimum);
        WorkerWhere(SettingCatalog.WorkerMaxExecutionDuration, 0).Validate();

        var retry = Definition(SettingCatalog.WorkerRetryDelay);
        WorkerWhere(SettingCatalog.WorkerRetryDelay, retry.Minimum).Validate();
        Assert.ThrowsAny<Exception>(
            () => WorkerWhere(SettingCatalog.WorkerRetryDelay, retry.Minimum - 1).Validate());
    }

    private static ParseRunWorkerOptions WorkerWhere(string key, long milliseconds)
    {
        var defaults = new ParseRunWorkerOptions();
        return new ParseRunWorkerOptions
        {
            MaintenanceInterval = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.WorkerMaintenanceInterval,
                milliseconds,
                defaults.MaintenanceInterval.TotalMilliseconds)),
            RecoveryBatchSize = (int)Pick(
                key,
                SettingCatalog.WorkerRecoveryBatchSize,
                milliseconds,
                defaults.RecoveryBatchSize),
            LeaseDuration = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.WorkerLeaseDuration,
                milliseconds,
                defaults.LeaseDuration.TotalMilliseconds)),
            HeartbeatInterval = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.WorkerHeartbeatInterval,
                milliseconds,
                defaults.HeartbeatInterval.TotalMilliseconds)),
            RetryDelay = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.WorkerRetryDelay,
                milliseconds,
                defaults.RetryDelay.TotalMilliseconds)),
            MinimumPollDelay = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.WorkerMinimumPollDelay,
                milliseconds,
                defaults.MinimumPollDelay.TotalMilliseconds)),
            MaximumPollDelay = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.WorkerMaximumPollDelay,
                milliseconds,
                defaults.MaximumPollDelay.TotalMilliseconds)),
            MaxExecutionDuration = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.WorkerMaxExecutionDuration,
                milliseconds,
                defaults.MaxExecutionDuration.TotalMilliseconds)),
        };
    }

    private static double Pick(string key, string candidate, long value, double fallback) =>
        key == candidate ? value : fallback;

    private static SettingDefinition Definition(string key)
    {
        return SettingCatalog.Find(key)
            ?? throw new InvalidOperationException($"'{key}' is missing from the catalog.");
    }
}
