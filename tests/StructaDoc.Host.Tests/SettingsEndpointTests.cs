using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Settings;
using StructaDoc.Infrastructure.ControlPlane;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Settings decide what the service does, so what may be written and what wins between the browser
/// and the deployment matter more than the round trip.
/// </summary>
public sealed class SettingsEndpointTests
{
    private const string Username = "settings-admin";
    private const string Password = "StructaDoc-Settings-2026!";

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
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var written = await response.Content.ReadFromJsonAsync<SettingUpdateResponse>();
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
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, "2048"));
        stored.EnsureSuccessStatusCode();
        Assert.Equal("2048", (await GetAsync(client, SettingCatalog.MaxUploadBytes)).Value);

        using var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.MaxUploadBytes, string.Empty));
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
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "4"));
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
                new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, string.Empty));
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
            new SettingUpdateRequest(SettingCatalog.ParseExecutionEnabled, "true"));
        stored.EnsureSuccessStatusCode();

        using var cleared = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseExecutionEnabled, string.Empty));
        cleared.EnsureSuccessStatusCode();

        // The listener closed the gate, so the default is genuinely in force. The startup snapshot
        // still holds the value the deleted row had, and reporting from it would contradict what the
        // service is doing.
        var state = await GetAsync(client, SettingCatalog.ParseExecutionEnabled);
        Assert.Equal("false", state.Value);
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
                    ["--Worker:MaxConcurrency=7"]))));
        using var client = await SignedInClientAsync(factory);

        // Only the refusal is asserted here. The pin reaches this Host through the replaced
        // registration rather than through its configuration, so the value it reports is the one it
        // actually started with; what a real pin does to the value is the unit test's subject.
        Assert.True((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsManagedExternally);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "2"));

        // Storing a value the service would never use reads as a change that did not happen.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await GetAsync(client, SettingCatalog.ParseMaxConcurrency)).IsStored);
    }

    [Fact]
    public async Task Execution_can_be_switched_without_a_restart()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        // The same boolean arrives as "False" from appsettings.json and "false" from the store, so
        // a caller comparing the two spellings would read one of them wrongly.
        Assert.Equal("false", (await GetAsync(client, SettingCatalog.ParseExecutionEnabled)).Value);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseExecutionEnabled, "true"));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SettingUpdateResponse>();
        Assert.False(result!.RestartRequired);
        Assert.Equal("true", (await GetAsync(client, SettingCatalog.ParseExecutionEnabled)).Value);
    }

    [Theory]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "0")]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "65")]
    [InlineData(SettingCatalog.ParseMaxConcurrency, "many")]
    [InlineData(SettingCatalog.ParseExecutionEnabled, "yes")]
    [InlineData(SettingCatalog.MaxUploadBytes, "512")]
    public async Task Values_outside_a_settings_range_or_type_are_refused(string key, string value)
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SignedInClientAsync(factory);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(key, value));

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
        // catalog would let one session steer configuration no test covers.
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest("Database:ConnectionString", "Data Source=elsewhere.db"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Settings_are_closed_to_anonymous_callers_and_require_antiforgery()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();

        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync("/api/v1/admin/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = await SignedInClientAsync(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var withoutToken = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseExecutionEnabled, "true"));
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
    }

    [Fact]
    public async Task Restart_is_administrator_only_and_reports_what_brings_the_service_back()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();

        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.PostAsync("/api/v1/admin/system/restart", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var client = await SignedInClientAsync(factory);
        using var accepted = await client.PostAsync("/api/v1/admin/system/restart", null);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<RestartAcceptedResponse>();
        Assert.Contains("restart policy", body!.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
    }

    private static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var anonymousToken = await client.GetAntiforgeryTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(Username, Password)),
        };
        request.Headers.Add(anonymousToken.HeaderName, anonymousToken.RequestToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var token = await client.GetAntiforgeryTokenAsync();
        client.DefaultRequestHeaders.Remove(token.HeaderName);
        client.DefaultRequestHeaders.Add(token.HeaderName, token.RequestToken);
        return client;
    }

    /// <summary>
    /// One directory that outlives the Host, so a settings store can be observed across a restart
    /// rather than only within the process that wrote it.
    /// </summary>
    private sealed class SettingsTestDeployment : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-settings-tests",
            Guid.NewGuid().ToString("N"));

        public SettingsTestDeployment()
        {
            Directory.CreateDirectory(directory);
        }

        public WebApplicationFactory<Program> CreateFactory(
            Action<IWebHostBuilder>? configure = null)
        {
            return new SettingsTestFactory(directory, configure);
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class SettingsTestFactory(string directory, Action<IWebHostBuilder>? configure)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Worker:Enabled", "false");
            builder.UseSetting("Authentication:BootstrapAdministratorUsername", Username);
            builder.UseSetting("Authentication:BootstrapAdministratorPassword", Password);
            builder.UseSetting(
                "Authentication:DataProtectionKeysPath",
                Path.Combine(directory, "keys"));
            builder.UseSetting("Storage:Provider", "Local");
            builder.UseSetting("Storage:RootPath", Path.Combine(directory, "storage"));
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting(
                "Database:ConnectionString",
                $"Data Source={Path.Combine(directory, "structadoc.db")};Pooling=False");
            builder.UseSetting("ControlPlane:DatabasePath", Path.Combine(directory, "control.db"));
            configure?.Invoke(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
