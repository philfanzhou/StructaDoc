using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Contracts.Settings;
using StructaDoc.Adapters.ControlPlane;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Settings decide what the service does, so what may be written and what wins between the browser
/// and the deployment matter more than the round trip.
/// </summary>
public sealed class SettingsEndpointTests
{

    [Fact]
    public async Task A_stored_setting_replaces_the_shipped_default_and_survives_a_restart()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(factory))
        {
            var before = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
            Assert.False(before.IsStored);
            Assert.Equal("1", before.Value);
            Assert.False(before.IsManagedExternally);

            using var response = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var written = await response.Content.ReadFromJsonAsync<SettingUpdateResponse>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(written!.RestartRequired);
        }

        // A setting that only lived in the writing process would be a setting nobody set.
        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            var after = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
            Assert.True(after.IsStored);
            Assert.Equal("4", after.Value);
        }
    }

    [Fact]
    public async Task Clearing_a_setting_restores_the_default_rather_than_storing_nothing()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        using var stored = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, "2048"),
            cancellationToken: TestContext.Current.CancellationToken);
        stored.EnsureSuccessStatusCode();
        Assert.Equal("2048", (await GetAsync(client, SettingCatalog.MaxUploadBytes)).Value);

        using var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);
        cleared.EnsureSuccessStatusCode();

        var restored = await GetAsync(client, SettingCatalog.MaxUploadBytes);
        Assert.False(restored.IsStored);
        Assert.Equal("104857600", restored.Value);

        // Nothing is pending: this process started with no stored row, so the default it bound at
        // startup is the value the clear returns to.
        Assert.False(restored.IsPendingRestart);
    }

    [Fact]
    public async Task Clearing_a_setting_this_process_started_with_reports_the_default_as_pending()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(factory))
        {
            using var stored = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"),
                cancellationToken: TestContext.Current.CancellationToken);
            stored.EnsureSuccessStatusCode();
        }

        // This process bound its options from a configuration that included the row, so clearing it
        // must report the default that now applies and say the running service has not caught up.
        // Reading the startup snapshot instead would report the deleted value as still chosen.
        using (var restarted = deployment.CreateFactory())
        using (var client = await SignedInClientAsync(restarted))
        {
            Assert.Equal("4", (await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).Value);

            using var cleared = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, string.Empty),
                cancellationToken: TestContext.Current.CancellationToken);
            cleared.EnsureSuccessStatusCode();

            var state = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
            Assert.Equal("1", state.Value);
            Assert.False(state.IsStored);
            Assert.True(state.IsPendingRestart);
        }
    }

    [Fact]
    public async Task Clearing_a_live_setting_reports_the_default_rather_than_the_cleared_value()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        using var stored = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"),
            cancellationToken: TestContext.Current.CancellationToken);
        stored.EnsureSuccessStatusCode();
        Assert.True((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsPendingRestart);

        using var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);
        cleared.EnsureSuccessStatusCode();

        // Clearing restores the default, and the default is what the running process is already
        // using. Reporting from the startup snapshot alone would be right here by accident; reporting
        // the value the deleted row held would say a change is waiting that nothing is waiting for.
        var state = await GetAsync(client, SettingCatalog.ParseMaxConcurrency);
        Assert.Equal("1", state.Value);
        Assert.False(state.IsStored);
        Assert.False(state.IsPendingRestart);
    }

    [Fact]
    public async Task A_value_the_deployment_pins_cannot_be_written_here()
    {
        using var deployment = new SettingsTestDeployment();

        // A pin is an environment variable or a command-line argument, and both are process-wide.
        // The registration is replaced instead, so one test cannot change what a Host running beside
        // it sees. Precedence itself is covered where the rule lives, in
        // StructaDocSettingsConfigurationTests.
        using var factory = deployment.CreateFactory(builder => builder.ConfigureServices(
            (context, services) => services.AddSingleton(
                StructaDocSettingsConfiguration.Create(
                    context.Configuration,
                    new ControlPlaneOptions
                    {
                        DatabasePath = context.Configuration["ControlPlane:DatabasePath"]!,
                    },
                    ["--Worker:MaxConcurrency=7"],
                    new FakeSettingSecretProtector(),
                    new SettingsStartupFault()))));
        using var client = await SignedInClientAsync(factory);

        // Only the refusal is asserted here. The pin reaches this Host through the replaced
        // registration rather than through its configuration, so the value it reports is the one it
        // actually started with; what a real pin does to the value is the unit test's subject.
        Assert.True((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsManagedExternally);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "2"),
            cancellationToken: TestContext.Current.CancellationToken);

        // Storing a value the service would never use reads as a change that did not happen.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsStored);
    }

    [Fact]
    public async Task Parse_execution_status_reports_a_host_that_runs_no_workers()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        // This deployment starts a Host with no Workers, which is the one remaining way a Parse Run
        // queues behind nothing. It is a deployment choice and not settable here, so all the service
        // can do is report it -- and it has to, or a queue that will never move looks like one that
        // is about to.
        var status = await client
            .GetFromJsonAsync<ParseExecutionStatusResponse>(
                "/api/v1/parse-execution",
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(status!.WorkerEnabled);
    }

    [Fact]
    public async Task Parse_execution_status_is_not_readable_without_signing_in()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/parse-execution",
            TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Unauthenticated access returned {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "0")]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "65")]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "many")]
    [InlineData(SettingCatalog.UploadApiEnabled, "yes")]
    [InlineData(SettingCatalog.MaxUploadBytes, "512")]
    public async Task Values_outside_a_settings_range_or_type_are_refused(string key, string value)
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(key, value),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetAsync(client, key)).IsStored);
    }

    [Fact]
    public async Task Keys_outside_the_catalog_cannot_be_written()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        // Settings are an allowlist. A key that could reach the store without appearing in the
        // catalog would let one session steer configuration no test covers. This one is refused for
        // a second reason as well: the key ring it locates is read before the store is opened, so a
        // stored value would be read too early to have any effect.
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(
                "Authentication:DataProtectionKeysPath",
                "/tmp/somewhere-else"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Settings_are_closed_to_anonymous_callers_and_require_antiforgery()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();

        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(
            "/api/v1/admin/settings",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = await SignedInClientAsync(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var withoutToken = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.UploadApiEnabled, "false"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
    }

    [Fact]
    public async Task Restart_is_administrator_only_and_reports_what_brings_the_service_back()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();

        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.PostAsync(
            "/api/v1/admin/system/restart",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var client = await SignedInClientAsync(factory);
        using var accepted = await client.PostAsync(
            "/api/v1/admin/system/restart",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<RestartAcceptedResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("restart policy", body!.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
    }

    private static Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory)
    {
        return SettingsTestDeployment.SignedInClientAsync(factory);
    }
}
