using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Sign-in through an identity provider is the only way an end user reaches the workspace, so a
/// deployment that cannot configure it from the browser has no users. What matters here is that
/// configuring it wrongly stays recoverable from the same browser.
/// </summary>
public sealed class OidcSettingsEndpointTests
{
    [Fact]
    public async Task A_client_secret_is_stored_encrypted_and_never_read_back()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        const string Secret = "the-client-secret-nobody-should-see";
        using var written = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.OidcClientSecret, Secret));
        written.EnsureSuccessStatusCode();

        var state = await GetAsync(client, SettingCatalog.OidcClientSecret);

        // Whether it is set is the whole of what an administration page needs. Sending the value back
        // would mean an intercepted response gave up a credential the reader never wrote.
        Assert.True(state.IsStored);
        Assert.Equal(string.Empty, state.Value);

        // The database file travels with every backup, so the row must not carry the secret either.
        var row = ReadRow(deployment.ControlPlanePath, SettingCatalog.OidcClientSecret);
        Assert.NotNull(row);
        Assert.DoesNotContain(Secret, row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_secret_survives_a_restart_and_reaches_configuration()
    {
        using var deployment = new SettingsTestDeployment();
        const string Secret = "round-trip-client-secret";

        using (var factory = deployment.CreateFactory())
        using (var client = await SettingsTestDeployment.SignedInClientAsync(factory))
        {
            using var written = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.OidcClientSecret, Secret));
            written.EnsureSuccessStatusCode();
        }

        // Encrypting is only useful if the service can still read it. A secret that survived storage
        // but not startup would be sent to the identity provider as an empty client secret.
        using (var restarted = deployment.CreateFactory())
        using (var client = await SettingsTestDeployment.SignedInClientAsync(restarted))
        {
            var settings = restarted.Services
                .GetRequiredService<Infrastructure.ControlPlane.StructaDocSettingsConfiguration>();
            Assert.Equal(Secret, settings.Effective[SettingCatalog.OidcClientSecret]);

            var state = await GetAsync(client, SettingCatalog.OidcClientSecret);
            Assert.True(state.IsStored);
            Assert.False(state.IsPendingRestart);
            Assert.Equal(string.Empty, state.Value);
        }
    }

    [Fact]
    public async Task An_authority_keeps_the_spelling_the_identity_provider_uses()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        using var written = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.OidcAuthority, "https://issuer.example/realm/  "));
        written.EnsureSuccessStatusCode();

        // The trailing slash is the difference most misconfigurations come down to: the middleware
        // compares the authority with the issuer a provider reports, and the two spellings address
        // the same provider.
        Assert.Equal(
            "https://issuer.example/realm",
            (await GetAsync(client, SettingCatalog.OidcAuthority)).Value);
    }

    [Theory]
    [InlineData("issuer.example")]
    [InlineData("ftp://issuer.example")]
    [InlineData("https://issuer.example\nX-Injected: 1")]
    public async Task An_address_the_service_could_not_use_is_refused(string authority)
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.OidcAuthority, authority));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetAsync(client, SettingCatalog.OidcAuthority)).IsStored);
    }

    [Fact]
    public async Task A_stored_configuration_that_cannot_start_leaves_the_service_running()
    {
        using var deployment = new SettingsTestDeployment();

        using (var factory = deployment.CreateFactory())
        using (var client = await SettingsTestDeployment.SignedInClientAsync(factory))
        {
            // Reachable through the API rather than contrived: settings are written one key at a
            // time, so enabling sign-in before filling in the authority is an ordinary order to do
            // it in. The combination is what fails, and no single write can see it coming.
            using var enabled = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.OidcEnabled, "true"));
            enabled.EnsureSuccessStatusCode();
        }

        // Refusing to start here would take away the only surface this deployment could be fixed
        // from. Signing in at all is the assertion that it did not.
        using (var restarted = deployment.CreateFactory())
        using (var client = await SettingsTestDeployment.SignedInClientAsync(restarted))
        {
            var status = await client.GetFromJsonAsync<OidcStatusResponse>(
                "/api/v1/admin/settings/oidc");

            Assert.False(status!.Enabled);
            Assert.NotNull(status.StartupFault);

            // The stored value is still reported, because the administrator has to see what they
            // wrote to correct it. The fault is what says it is not in effect.
            Assert.True((await GetAsync(client, SettingCatalog.OidcEnabled)).IsStored);
        }
    }

    [Fact]
    public async Task A_reachable_authority_is_confirmed_by_its_own_discovery_document()
    {
        await using var provider = await StubIdentityProvider.StartAsync();
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        var result = await TestAsync(client, provider.Authority);

        Assert.True(result.Succeeded);
        Assert.Equal(OidcDiscoveryCodes.Reachable, result.Code);
        Assert.Equal(provider.Authority, result.Issuer);
    }

    [Fact]
    public async Task An_authority_that_disagrees_with_its_own_issuer_is_reported()
    {
        await using var provider = await StubIdentityProvider.StartAsync(
            issuer: "https://somewhere.else.example");
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        // Sign-in would fail on every token with no explanation an administrator could act on, and
        // only once a real user tried it.
        var result = await TestAsync(client, provider.Authority);

        Assert.False(result.Succeeded);
        Assert.Equal(OidcDiscoveryCodes.IssuerMismatch, result.Code);
        Assert.Equal("https://somewhere.else.example", result.Issuer);
    }

    [Fact]
    public async Task Something_that_is_not_an_identity_provider_is_reported_as_such()
    {
        await using var provider = await StubIdentityProvider.StartAsync(document: "{\"hello\":1}");
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        var result = await TestAsync(client, provider.Authority);

        Assert.Equal(OidcDiscoveryCodes.IncompleteDocument, result.Code);
    }

    [Fact]
    public async Task An_address_nothing_answers_is_reported_rather_than_thrown()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        // Port 1 is reserved and nothing listens on it, so this fails to connect rather than
        // hanging until a timeout.
        var result = await TestAsync(client, "http://127.0.0.1:1");

        Assert.False(result.Succeeded);
        Assert.Equal(OidcDiscoveryCodes.Unreachable, result.Code);
    }

    [Fact]
    public async Task A_plain_http_authority_is_refused_while_secure_metadata_is_required()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/oidc/test",
            new OidcConnectionTestRequest("http://issuer.example", RequireHttpsMetadata: true));
        var result = await response.Content.ReadFromJsonAsync<OidcConnectionTestResponse>();

        // Reporting it here rather than after a fetch keeps the answer the same as the one the
        // service would give itself at startup.
        Assert.Equal(OidcDiscoveryCodes.InsecureAuthority, result!.Code);
    }

    [Fact]
    public async Task The_identity_provider_endpoints_are_administrator_only()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();

        using var anonymous = factory.CreateClient();
        using var status = await anonymous.GetAsync("/api/v1/admin/settings/oidc");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);

        using var probe = await anonymous.PostAsJsonAsync(
            "/api/v1/admin/settings/oidc/test",
            new OidcConnectionTestRequest("https://issuer.example", RequireHttpsMetadata: true));
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);

        // The probe makes the service fetch an address the caller chose, so it is a write as far as
        // cross-site requests are concerned.
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var withoutToken = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/oidc/test",
            new OidcConnectionTestRequest("https://issuer.example", RequireHttpsMetadata: true));
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
    }

    private static async Task<OidcConnectionTestResponse> TestAsync(
        HttpClient client,
        string authority)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/oidc/test",
            new OidcConnectionTestRequest(authority, RequireHttpsMetadata: false));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OidcConnectionTestResponse>())!;
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
    }

    private static string? ReadRow(string controlPlanePath, string key)
    {
        using var connection = new SqliteConnection($"Data Source={controlPlanePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}
