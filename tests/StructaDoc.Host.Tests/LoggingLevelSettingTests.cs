using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Logging;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The three console log levels are settings the running service re-reads: a change listener
/// flips the level switch the pipeline filters at, so a stored write changes what the same process
/// logs without a restart. These tests hold that promise end to end through the settings API and
/// the console the pipeline writes to, and the two boundaries around it: a key the deployment
/// pins is read-only, and a chosen level is what the next process starts filtering at.
/// </summary>
[Collection("Console output")]
public sealed class LoggingLevelSettingTests
{
    [Fact]
    public async Task Changing_the_log_level_moves_console_filtering_without_a_restart()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        string whileWarningIsInForce;
        try
        {
            Console.SetOut(capture);
            // The listener's own confirmation is an Information event on a category the root
            // switch governs, which makes it the marker that proves which way the filter points:
            // lowering the root to Warning takes it off the console, and raising it back puts it
            // on again — on the same process, with nothing rebuilt.
            await WriteAsync(client, SettingCatalog.LoggingDefaultLevel, "Warning");
            await WriteAsync(client, SettingCatalog.LoggingEfCoreDatabaseCommandLevel, "Error");
            whileWarningIsInForce = capture.Snapshot();

            await WriteAsync(client, SettingCatalog.LoggingDefaultLevel, "Information");
            await WriteAsync(client, SettingCatalog.LoggingEfCoreDatabaseCommandLevel, "Warning");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var output = capture.Snapshot();
        Assert.DoesNotContain("changed to 'Error'", whileWarningIsInForce, StringComparison.Ordinal);
        Assert.Contains("changed to 'Information'", output, StringComparison.Ordinal);
        Assert.Contains("changed to 'Warning'", output, StringComparison.Ordinal);

        var root = await GetAsync(client, SettingCatalog.LoggingDefaultLevel);
        Assert.Equal("Information", root.Value);
        Assert.True(root.IsStored);
        Assert.False(root.RequiresRestart);
        Assert.False(root.IsPendingRestart);

        var ef = await GetAsync(client, SettingCatalog.LoggingEfCoreDatabaseCommandLevel);
        Assert.Equal("Warning", ef.Value);
        Assert.True(ef.IsStored);
        Assert.False(ef.IsPendingRestart);
    }

    [Fact]
    public async Task A_deployment_pin_is_reported_and_refused_rather_than_written_over()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory(builder =>
            builder.UseSetting(SettingCatalog.LoggingDefaultLevel, "Debug"));
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        // The pin reaches this host as a command-line argument, which is what a deployment pin
        // is. It seeds the switch the pipeline started filtering at, so the page reports the
        // pinned level as what is running and refuses to store a value the service would never
        // read.
        var pinned = await GetAsync(client, SettingCatalog.LoggingDefaultLevel);
        Assert.True(pinned.IsManagedExternally);
        Assert.Equal("Debug", pinned.Value);
        Assert.False(pinned.IsStored);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.LoggingDefaultLevel, "Warning"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await GetAsync(client, SettingCatalog.LoggingDefaultLevel)).IsStored);

        // The seeded switch is the running filter: the pin, not the shipped default.
        var switches = factory.Services.GetRequiredService<StructaDocConsoleLogSwitches>();
        Assert.True(switches.TryGet(SettingCatalog.LoggingDefaultLevel, out var seeded));
        Assert.Equal("Debug", seeded);
    }

    [Fact]
    public async Task A_stored_level_is_what_the_next_process_starts_filtering_at()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SettingsTestDeployment.SignedInClientAsync(factory))
        {
            await WriteAsync(client, SettingCatalog.LoggingAspNetCoreLevel, "Error");
        }

        using var restarted = deployment.CreateFactory();
        using (var client = await SettingsTestDeployment.SignedInClientAsync(restarted))
        {
            var state = await GetAsync(client, SettingCatalog.LoggingAspNetCoreLevel);
            Assert.Equal("Error", state.Value);
            Assert.True(state.IsStored);
            // The new process seeded its switch from the stored row and the listener reports that
            // same switch, so nothing is pending: what was chosen is what is running.
            Assert.False(state.RequiresRestart);
            Assert.False(state.IsPendingRestart);
        }

        var switches = restarted.Services.GetRequiredService<StructaDocConsoleLogSwitches>();
        Assert.True(switches.TryGet(SettingCatalog.LoggingAspNetCoreLevel, out var seeded));
        Assert.Equal("Error", seeded);
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
    }

    private static async Task WriteAsync(HttpClient client, string key, string value)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(key, value),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Writing '{key}' returned {(int)response.StatusCode}.");
        var written = await response.Content.ReadFromJsonAsync<SettingUpdateResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(written!.RestartRequired, $"Writing '{key}' asked for a restart.");
    }
}
