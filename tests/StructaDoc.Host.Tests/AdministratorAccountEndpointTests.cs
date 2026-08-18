using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Tests;

/// <summary>
/// A deployment of its own, because verifying that accounts can sign in takes far more sign-ins than
/// the per-address login rate limit allows a real administrator.
/// </summary>
public sealed class AdministratorAccountTestFactory : WebApplicationFactory<Program>
{
    public const string AdministratorUsername = "account-admin";
    public const string AdministratorPassword = "StructaDoc-Account-Password-2026!";

    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "structadoc-account-tests",
        Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(testDirectory);
        builder.UseSetting("Worker:Enabled", "false");
        builder.UseSetting("Authentication:LoginPermitLimit", "1000");
        builder.UseSetting("Authentication:BootstrapAdministratorUsername", AdministratorUsername);
        builder.UseSetting("Authentication:BootstrapAdministratorPassword", AdministratorPassword);
        builder.UseSetting("Authentication:BootstrapAdministratorDisplayName", "Account Owner");
        builder.UseSetting(
            "Authentication:DataProtectionKeysPath",
            Path.Combine(testDirectory, "keys"));
        builder.UseSetting("Storage:Provider", "Local");
        builder.UseSetting("Storage:RootPath", Path.Combine(testDirectory, "storage"));
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting(
            "Database:ConnectionString",
            $"Data Source={Path.Combine(testDirectory, "structadoc.db")};Pooling=False");
        builder.UseSetting("ControlPlane:DatabasePath", Path.Combine(testDirectory, "control.db"));
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

/// <summary>
/// Account administration is the one place that can lock a deployment out of itself, so its refusals
/// matter as much as its mutations.
/// </summary>
public sealed class AdministratorAccountEndpointTests
    : IClassFixture<AdministratorAccountTestFactory>
{
    private const string SecondPassword = "StructaDoc-Second-Password-2026!";

    private readonly AdministratorAccountTestFactory factory;

    public AdministratorAccountEndpointTests(AdministratorAccountTestFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Listing_marks_the_calling_administrator()
    {
        using var client = factory.CreateClient();
        await SignInAsOwnerAsync(client);

        var accounts = await client.GetFromJsonAsync<AdministratorAccountResponse[]>(
            "/api/v1/admin/administrators",
            cancellationToken: TestContext.Current.CancellationToken);

        var current = Assert.Single(accounts!, account => account.IsCurrent);
        Assert.Equal(AdministratorAccountTestFactory.AdministratorUsername, current.Username);
        Assert.True(current.IsActive);
    }

    [Fact]
    public async Task Created_administrator_can_sign_in_and_the_username_cannot_be_reused()
    {
        using var client = factory.CreateClient();
        await SignInAsOwnerAsync(client);
        var username = UniqueUsername();

        using var created = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(username, SecondPassword, "Second operator"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var newClient = factory.CreateClient();
        await SignInAsync(newClient, username, SecondPassword);
        var session = await newClient.GetFromJsonAsync<AdministratorSessionResponse>(
            "/api/v1/admin/session",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(username, session!.Username);

        using var duplicate = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(username.ToUpperInvariant(), SecondPassword, null),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Theory]
    [InlineData("no", SecondPassword)]
    [InlineData("bad name", SecondPassword)]
    [InlineData("valid-name", "7-chars")]
    public async Task Creation_rejects_values_outside_the_username_and_password_policy(
        string username,
        string password)
    {
        using var client = factory.CreateClient();
        await SignInAsOwnerAsync(client);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(username, password, null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_shortest_accepted_password_is_eight_characters()
    {
        using var client = factory.CreateClient();
        await SignInAsOwnerAsync(client);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(UniqueUsername(), "8-char-x", null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_keeps_the_caller_signed_in_and_ends_its_other_sessions()
    {
        var username = UniqueUsername();
        using var administrator = factory.CreateClient();
        await SignInAsOwnerAsync(administrator);
        using var created = await administrator.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(username, SecondPassword, null),
            cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();

        using var first = factory.CreateClient();
        await SignInAsync(first, username, SecondPassword);
        using var second = factory.CreateClient();
        await SignInAsync(second, username, SecondPassword);

        var replacement = SecondPassword + "-changed";
        using var change = await first.PostAsJsonAsync(
            "/api/v1/admin/administrators/me/password",
            new ChangeOwnPasswordRequest(SecondPassword, replacement),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // The stamp rotated, so the session that made the change is re-issued and the other one is
        // not: a password change that left old sessions alive would not be a revocation.
        using var callerSession = await first.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, callerSession.StatusCode);
        using var otherSession = await second.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, otherSession.StatusCode);

        using var withOldPassword = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await SignInStatusAsync(withOldPassword, username, SecondPassword));
        using var withNewPassword = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            await SignInStatusAsync(withNewPassword, username, replacement));
    }

    [Fact]
    public async Task Changing_a_password_requires_the_current_one()
    {
        var username = UniqueUsername();
        using var administrator = factory.CreateClient();
        await SignInAsOwnerAsync(administrator);
        using var created = await administrator.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(username, SecondPassword, null),
            cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();

        using var client = factory.CreateClient();
        await SignInAsync(client, username, SecondPassword);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators/me/password",
            new ChangeOwnPasswordRequest("not-the-current-password", SecondPassword + "-changed"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var stillSignedIn = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
    }

    [Fact]
    public async Task Resetting_another_password_ends_that_administrators_sessions()
    {
        var username = UniqueUsername();
        using var administrator = factory.CreateClient();
        await SignInAsOwnerAsync(administrator);
        var account = await CreateAsync(administrator, username);

        using var victim = factory.CreateClient();
        await SignInAsync(victim, username, SecondPassword);

        using var reset = await administrator.PostAsJsonAsync(
            $"/api/v1/admin/administrators/{account.Id:D}/password",
            new ResetAdministratorPasswordRequest(SecondPassword + "-reset"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var afterReset = await victim.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReset.StatusCode);
    }

    [Fact]
    public async Task Resetting_your_own_password_is_refused_because_it_would_skip_the_current_one()
    {
        using var client = factory.CreateClient();
        await SignInAsOwnerAsync(client);
        var accounts = await client.GetFromJsonAsync<AdministratorAccountResponse[]>(
            "/api/v1/admin/administrators",
            cancellationToken: TestContext.Current.CancellationToken);
        var self = accounts!.Single(account => account.IsCurrent);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/administrators/{self.Id:D}/password",
            new ResetAdministratorPasswordRequest(SecondPassword),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Disabling_an_administrator_ends_its_sessions_and_blocks_sign_in()
    {
        var username = UniqueUsername();
        using var administrator = factory.CreateClient();
        await SignInAsOwnerAsync(administrator);
        var account = await CreateAsync(administrator, username);

        using var disabled = factory.CreateClient();
        await SignInAsync(disabled, username, SecondPassword);

        using var response = await administrator.PutAsJsonAsync(
            $"/api/v1/admin/administrators/{account.Id:D}/active",
            new SetAdministratorActiveRequest(false),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var afterDisable = await disabled.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDisable.StatusCode);
        using var signIn = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await SignInStatusAsync(signIn, username, SecondPassword));

        using var enable = await administrator.PutAsJsonAsync(
            $"/api/v1/admin/administrators/{account.Id:D}/active",
            new SetAdministratorActiveRequest(true),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        using var reEnabled = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            await SignInStatusAsync(reEnabled, username, SecondPassword));
    }

    [Fact]
    public async Task An_administrator_cannot_disable_or_delete_itself()
    {
        using var client = factory.CreateClient();
        await SignInAsOwnerAsync(client);
        var accounts = await client.GetFromJsonAsync<AdministratorAccountResponse[]>(
            "/api/v1/admin/administrators",
            cancellationToken: TestContext.Current.CancellationToken);
        var self = accounts!.Single(account => account.IsCurrent);

        using var disable = await client.PutAsJsonAsync(
            $"/api/v1/admin/administrators/{self.Id:D}/active",
            new SetAdministratorActiveRequest(false),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);

        using var delete = await client.DeleteAsync(
            $"/api/v1/admin/administrators/{self.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);

        using var stillSignedIn = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
    }

    [Fact]
    public async Task Deleted_administrators_disappear_and_cannot_sign_in()
    {
        var username = UniqueUsername();
        using var administrator = factory.CreateClient();
        await SignInAsOwnerAsync(administrator);
        var account = await CreateAsync(administrator, username);

        using var response = await administrator.DeleteAsync(
            $"/api/v1/admin/administrators/{account.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var accounts = await administrator.GetFromJsonAsync<AdministratorAccountResponse[]>(
            "/api/v1/admin/administrators",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(accounts!, candidate => candidate.Id == account.Id);

        using var signIn = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await SignInStatusAsync(signIn, username, SecondPassword));

        using var again = await administrator.DeleteAsync(
            $"/api/v1/admin/administrators/{account.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Two_administrators_removing_each_other_at_once_cannot_empty_the_deployment()
    {
        // One request at a time can never reach this: the only active administrator is always the
        // caller, and an administrator may not remove itself. Two of them disabling each other
        // simultaneously is the case that rule cannot cover, so the invariant travels into the
        // statement instead of being read first.
        using var isolated = new AdministratorAccountTestFactory();
        using var owner = isolated.CreateClient();
        await SignInAsOwnerAsync(owner);
        var ownerAccount = Assert.Single(
            (await owner.GetFromJsonAsync<AdministratorAccountResponse[]>(
                "/api/v1/admin/administrators",
                cancellationToken: TestContext.Current.CancellationToken))!);

        var deputyName = UniqueUsername();
        var deputyAccount = await CreateAsync(owner, deputyName);
        using var deputy = isolated.CreateClient();
        await SignInAsync(deputy, deputyName, SecondPassword);

        var ownerDisablesDeputy = owner.PutAsJsonAsync(
            $"/api/v1/admin/administrators/{deputyAccount.Id:D}/active",
            new SetAdministratorActiveRequest(false),
            cancellationToken: TestContext.Current.CancellationToken);
        var deputyDisablesOwner = deputy.PutAsJsonAsync(
            $"/api/v1/admin/administrators/{ownerAccount.Id:D}/active",
            new SetAdministratorActiveRequest(false),
            cancellationToken: TestContext.Current.CancellationToken);
        var responses = await Task.WhenAll(ownerDisablesDeputy, deputyDisablesOwner);

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        // The winner disabled the other account, so the winner is the one still able to sign in.
        var ownerWon = responses[0].StatusCode == HttpStatusCode.NoContent;
        using var survivor = isolated.CreateClient();
        if (ownerWon)
        {
            await SignInAsOwnerAsync(survivor);
        }
        else
        {
            await SignInAsync(survivor, deputyName, SecondPassword);
        }

        var remaining = await survivor.GetFromJsonAsync<AdministratorAccountResponse[]>(
            "/api/v1/admin/administrators",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(remaining!, account => account.IsActive);
    }

    [Fact]
    public async Task A_disabled_administrator_can_still_be_deleted()
    {
        var username = UniqueUsername();
        using var administrator = factory.CreateClient();
        await SignInAsOwnerAsync(administrator);
        var account = await CreateAsync(administrator, username);

        using var disable = await administrator.PutAsJsonAsync(
            $"/api/v1/admin/administrators/{account.Id:D}/active",
            new SetAdministratorActiveRequest(false),
            cancellationToken: TestContext.Current.CancellationToken);
        disable.EnsureSuccessStatusCode();

        // An inactive account is never the last active one, so the guard must not hold it hostage.
        using var delete = await administrator.DeleteAsync(
            $"/api/v1/admin/administrators/{account.Id:D}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Account_mutations_require_an_antiforgery_token()
    {
        using var client = factory.CreateClient();
        await SignInAsOwnerAsync(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(UniqueUsername(), SecondPassword, null),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Account_administration_is_closed_to_anonymous_callers()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/admin/administrators",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task SignInAsOwnerAsync(HttpClient client)
    {
        return SignInAsync(
            client,
            AdministratorAccountTestFactory.AdministratorUsername,
            AdministratorAccountTestFactory.AdministratorPassword);
    }

    private static string UniqueUsername()
    {
        return "operator-" + Guid.NewGuid().ToString("N")[..12];
    }

    private static async Task<AdministratorAccountResponse> CreateAsync(
        HttpClient client,
        string username)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/administrators",
            new CreateAdministratorRequest(username, SecondPassword, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdministratorAccountResponse>())!;
    }

    private static async Task SignInAsync(HttpClient client, string username, string password)
    {
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(client, username, password));
    }

    private static async Task<HttpStatusCode> SignInStatusAsync(
        HttpClient client,
        string username,
        string password)
    {
        var anonymousToken = await client.GetAntiforgeryTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(username, password)),
        };
        request.Headers.Add(anonymousToken.HeaderName, anonymousToken.RequestToken);
        using var response = await client.SendAsync(request);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return response.StatusCode;
        }

        var authenticatedToken = await client.GetAntiforgeryTokenAsync();
        client.DefaultRequestHeaders.Remove(authenticatedToken.HeaderName);
        client.DefaultRequestHeaders.Add(
            authenticatedToken.HeaderName,
            authenticatedToken.RequestToken);
        return HttpStatusCode.OK;
    }
}
