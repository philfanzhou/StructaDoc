using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Setup;

namespace StructaDoc.Host.Tests;

/// <summary>
/// First-run setup is the only anonymous endpoint that can create an administrator, so its closing
/// conditions matter more than its happy path.
/// </summary>
public sealed class SetupEndpointTests
{
    private const string Username = "first-operator";
    private const string Password = "StructaDoc-Setup-Password-2026!";

    [Fact]
    public async Task Setup_is_required_until_it_is_claimed_and_then_stops_existing()
    {
        using var factory = new UnclaimedFactory();
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<SetupStatusResponse>(
            "/api/v1/setup",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(before!.SetupRequired);

        var token = await client.GetAntiforgeryTokenAsync();
        using var claim = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup")
        {
            Content = JsonContent.Create(new SetupClaimRequest(Username, Password, "First operator")),
        };
        claim.Headers.Add(token.HeaderName, token.RequestToken);
        using var claimResponse = await client.SendAsync(
            claim,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, claimResponse.StatusCode);

        var after = await client.GetFromJsonAsync<SetupStatusResponse>(
            "/api/v1/setup",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(after!.SetupRequired);

        // A second caller must not be able to add itself through the anonymous endpoint.
        using var secondClient = factory.CreateClient();
        var secondToken = await secondClient.GetAntiforgeryTokenAsync();
        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup")
        {
            Content = JsonContent.Create(new SetupClaimRequest("second-operator", Password, null)),
        };
        second.Headers.Add(secondToken.HeaderName, secondToken.RequestToken);
        using var secondResponse = await secondClient.SendAsync(
            second,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Claim_signs_the_new_administrator_in_and_reports_the_claimant()
    {
        using var factory = new UnclaimedFactory();
        using var client = factory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();
        using var claim = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup")
        {
            Content = JsonContent.Create(new SetupClaimRequest(Username, Password, null)),
        };
        claim.Headers.Add(token.HeaderName, token.RequestToken);
        using var claimResponse = await client.SendAsync(
            claim,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, claimResponse.StatusCode);

        var session = await client.GetFromJsonAsync<AdministratorSessionResponse>(
            "/api/v1/admin/session",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Username, session!.Username);

        var warning = await client.GetFromJsonAsync<SetupClaimWarningResponse>(
            "/api/v1/admin/setup-claim",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(warning!.ClaimedFromAddress));

        // The warning is the compensating control for an unauthenticated claim, so it stays until an
        // administrator confirms it rather than expiring on its own.
        var acknowledgeToken = await client.GetAntiforgeryTokenAsync();
        using var acknowledge = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/admin/setup-claim/acknowledge");
        acknowledge.Headers.Add(acknowledgeToken.HeaderName, acknowledgeToken.RequestToken);
        using var acknowledgeResponse = await client.SendAsync(
            acknowledge,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, acknowledgeResponse.StatusCode);

        using var afterAcknowledge = await client.GetAsync(
            "/api/v1/admin/setup-claim",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, afterAcknowledge.StatusCode);
    }

    [Theory]
    [InlineData("ab", Password)]
    [InlineData("has space", Password)]
    [InlineData("-leading", Password)]
    [InlineData("valid-name", "short")]
    public async Task Claim_rejects_values_outside_the_username_and_password_policy(
        string username,
        string password)
    {
        using var factory = new UnclaimedFactory();
        using var client = factory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup")
        {
            Content = JsonContent.Create(new SetupClaimRequest(username, password, null)),
        };
        request.Headers.Add(token.HeaderName, token.RequestToken);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await client.GetFromJsonAsync<SetupStatusResponse>(
            "/api/v1/setup",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(status!.SetupRequired);
    }

    [Fact]
    public async Task Claim_requires_an_antiforgery_token()
    {
        using var factory = new UnclaimedFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/setup",
            new SetupClaimRequest(Username, Password, null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Configured_bootstrap_credentials_close_setup_without_a_browser()
    {
        // Unattended deployments provision through configuration, which must leave nothing for an
        // anonymous caller to claim.
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<SetupStatusResponse>(
            "/api/v1/setup",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(status!.SetupRequired);
    }

    /// <summary>
    /// The shared factory provisions an administrator from configuration, which is exactly the state
    /// first-run setup must not be in.
    /// </summary>
    private sealed class UnclaimedFactory : WebApplicationFactory<Program>
    {
        private readonly string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-setup-tests",
            Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(testDirectory);
            builder.UseSetting("Worker:Enabled", "false");
            builder.UseSetting("Authentication:BootstrapAdministratorUsername", string.Empty);
            builder.UseSetting("Authentication:BootstrapAdministratorPassword", string.Empty);
            builder.UseSetting(
                "Authentication:DataProtectionKeysPath",
                Path.Combine(testDirectory, "keys"));
            builder.UseSetting("Storage:Provider", "Local");
            builder.UseSetting("Storage:RootPath", Path.Combine(testDirectory, "storage"));
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting(
                "Database:ConnectionString",
                $"Data Source={Path.Combine(testDirectory, "structadoc.db")};Pooling=False");
            builder.UseSetting(
                "ControlPlane:DatabasePath",
                Path.Combine(testDirectory, "control.db"));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && Directory.Exists(testDirectory))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
