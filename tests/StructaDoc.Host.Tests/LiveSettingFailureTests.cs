using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// A change listener that fails must not pass silently: the stored value stays, the write is
/// answered with the restart it still needs, the read reports the change as pending, and the log
/// carries the failure. The console is swapped for the request because the ServiceMantle pipeline
/// is the only logging path — there is no other provider to read the event from.
/// </summary>
[Collection("Console output")]
public sealed class LiveSettingFailureTests
{
    [Fact]
    public async Task A_listener_that_fails_to_apply_reports_a_restart_and_logs_the_failure()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory(builder => builder.ConfigureServices(services =>
        {
            // The real listeners are replaced with one that owns the key but blows up applying it,
            // which is the state a bug or a broken dependency leaves the service in. The stored row,
            // the write answer, the read, and the log each have to tell the truth about it.
            services.RemoveAll<ISettingChangeListener>();
            services.AddSingleton<ISettingChangeListener>(new ThrowingListener());
        }));

        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        string snapshot;
        try
        {
            using (var response = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"),
                cancellationToken: TestContext.Current.CancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var written = await response.Content.ReadFromJsonAsync<SettingUpdateResponse>(
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(written!.RestartRequired);
            }

            snapshot = capture.Snapshot();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var state = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
        Assert.Equal("4", state.Value);
        Assert.True(state.IsPendingRestart);

        // The failure is visible to whoever reads the container log: the message names the setting
        // that did not take effect, which is what turns a silent no-op into something actionable.
        Assert.Contains("Worker:MaxConcurrency", snapshot, StringComparison.Ordinal);
        Assert.Contains("failed", snapshot, StringComparison.Ordinal);
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
    }

    private sealed class ThrowingListener : ISettingChangeListener
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

            throw new InvalidOperationException("The listener is broken by the test.");
        }

        public bool TryGetAppliedValue(string key, out string? appliedValue)
        {
            appliedValue = null;
            return false;
        }
    }
}
