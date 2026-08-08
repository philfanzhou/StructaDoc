using StructaDoc.Application.Settings;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.Tests;

public sealed class ParseExecutionGateTests
{
    [Fact]
    public async Task The_gate_follows_its_own_setting_and_ignores_every_other_one()
    {
        var gate = new ParseExecutionGate(initiallyEnabled: false);

        Assert.True(await gate.TryApplyAsync(SettingCatalog.ParseExecutionEnabled, "true"));
        Assert.True(gate.IsOpen);

        Assert.True(await gate.TryApplyAsync(SettingCatalog.ParseExecutionEnabled, "false"));
        Assert.False(gate.IsOpen);

        // Reporting a key it does not handle as applied would let that setting claim an effect it
        // never had, and skip the restart the administrator needs to be told about.
        Assert.False(await gate.TryApplyAsync(SettingCatalog.ParseMaxConcurrency, "4"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-boolean")]
    public async Task An_unreadable_value_closes_the_gate(string? value)
    {
        // Execution sends documents to an external Provider, so anything short of an explicit yes
        // has to mean no.
        var gate = new ParseExecutionGate(initiallyEnabled: true);

        await gate.TryApplyAsync(SettingCatalog.ParseExecutionEnabled, value);

        Assert.False(gate.IsOpen);
    }
}

/// <summary>
/// The catalog restates Worker defaults that live in the options class, which sits in this assembly.
/// A restatement that drifts tells administrators the service is doing something it is not.
/// </summary>
public sealed class WorkerSettingDefaultTests
{
    [Fact]
    public void Worker_defaults_match_the_options_they_describe()
    {
        var options = new ParseRunWorkerOptions();

        Assert.Equal(
            options.ExecutionEnabled ? "true" : "false",
            SettingCatalog.Find(SettingCatalog.ParseExecutionEnabled)!.Default);
        Assert.Equal(
            options.MaxConcurrency.ToString(),
            SettingCatalog.Find(SettingCatalog.ParseMaxConcurrency)!.Default);
    }
}
