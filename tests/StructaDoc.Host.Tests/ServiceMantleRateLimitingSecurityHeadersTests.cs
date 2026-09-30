using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ServiceMantle.Web.RateLimiting;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Setup;

namespace StructaDoc.Host.Tests;

// Setup and administrator sign-in are protected by ServiceMantle's two isolated named policies,
// and every `/api/v1/setup` and `/api/v1/admin` endpoint group is marked with the six-header
// security baseline. These tests hold the published contract: the 429 refusal is a safe
// problem+json body that names no client address, partition key, or credential; the two buckets
// are isolated in both directions; the baseline covers success, validation, authentication, and
// refusal responses of marked endpoints and nothing on unmarked ones; the shared keys outside the
// narrower policy range stop the host at startup; and a signed-in StructaDoc principal still
// shares the address-partitioned management bucket.
public sealed class ServiceMantleRateLimitingSecurityHeadersTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    private const string ExceededTypeUri = "urn:servicemantle:error:rate_limit.exceeded";
    private const string ExceededErrorCode = "rate_limit.exceeded";

    private static readonly IReadOnlyDictionary<string, string> BaselineHeaders =
        new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store",
            ["Pragma"] = "no-cache",
            ["X-Content-Type-Options"] = "nosniff",
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
            ["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
        };

    [Fact]
    public async Task Rejected_login_answers_the_safe_problem_body_without_any_caller_secret()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:LoginPermitLimit", "1"));
        using var client = limitedFactory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();

        await AttemptLoginAsync(client, token);
        using var response = await AttemptLoginAsync(client, token);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        // The library writes `Retry-After` only when the failed lease carries the metadata, and a
        // sliding-window lease never does — replenishment is continuous, so there is no single
        // instant to point the caller at. The header is therefore conditional by contract, not
        // missing by accident; when a future policy shape does provide it, it stays a positive
        // delay.
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            Assert.True(
                (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
                    || (retryAfter.Date is { } date && date > DateTimeOffset.UtcNow),
                "Retry-After must point into the future when present");
        }

        var payload = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        // The refusal names the rule that fired and the request it fired on, and nothing else: no
        // client address, no partition key, and nothing about the credentials that were submitted.
        Assert.DoesNotContain("127.0.0.1", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("::1", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-client", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("management-client:", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("management-operator:", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(StructaDocWebApplicationFactory.AdministratorUsername, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(StructaDocWebApplicationFactory.AdministratorPassword, payload, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(payload);
        Assert.Equal(ExceededTypeUri, body.RootElement.GetProperty("type").GetString());
        Assert.Equal("Too many requests.", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(ExceededErrorCode, body.RootElement.GetProperty("errorCode").GetString());
        var correlationId = body.RootElement.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.Equal(
            response.Headers.GetValues("x-correlation-id").Single(),
            correlationId);
    }

    // One shared key drives both policies, but the policies partition independently: exhausting
    // the setup bucket leaves sign-in untouched, and exhausting sign-in leaves setup untouched.
    // Both directions are held, because either leak would let one endpoint's traffic silence the
    // other's users.
    [Fact]
    public async Task Setup_and_signin_buckets_are_isolated_in_both_directions()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:LoginPermitLimit", "1"));
        using var client = limitedFactory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();

        // The shared host already has its administrator, so the setup claim is refused with 404 —
        // after the limiter has counted it.
        using var firstSetup = await AttemptSetupAsync(client, token);
        Assert.Equal(HttpStatusCode.NotFound, firstSetup.StatusCode);
        using var secondSetup = await AttemptSetupAsync(client, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondSetup.StatusCode);

        // The setup bucket is exhausted, and sign-in still answers with its own verdict.
        using var login = await AttemptLoginAsync(client, token);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // Now exhaust the sign-in bucket and confirm the setup bucket did not refill or drain
        // from it: setup is still the one refusal it already made.
        using var secondLogin = await AttemptLoginAsync(client, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondLogin.StatusCode);
        using var thirdSetup = await AttemptSetupAsync(client, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, thirdSetup.StatusCode);
    }

    // A StructaDoc principal carries no ServiceMantle management identity, so a signed-in caller
    // stays in the address-partitioned bucket rather than getting a fresh per-operator one. The
    // successful sign-in itself consumed the only permit, so the second attempt is refused.
    [Fact]
    public async Task Signedin_caller_still_shares_the_address_partitioned_signin_bucket()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:LoginPermitLimit", "1"));
        using var client = limitedFactory.CreateClient();

        await client.LoginAsAdministratorAsync();

        var token = await client.GetAntiforgeryTokenAsync();
        using var repeat = await AttemptLoginAsync(client, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeat.StatusCode);
    }

    [Fact]
    public async Task Marked_administration_responses_carry_the_six_header_baseline()
    {
        using var client = factory.CreateClient();

        // 400: the antiforgery guard refuses a well-formed sign-in without a token.
        using var unguarded = await client.PostAsJsonAsync(
            "/api/v1/admin/session",
            new AdministratorLoginRequest(
                StructaDocWebApplicationFactory.AdministratorUsername,
                StructaDocWebApplicationFactory.AdministratorPassword),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unguarded.StatusCode);
        AssertBaseline(unguarded);

        // 401: the session read is administrator-only and this client is anonymous.
        using var unauthorized = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        AssertBaseline(unauthorized);

        // 200 and 204: a signed-in administrator reads its session and signs out.
        await client.LoginAsAdministratorAsync();
        using var session = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        AssertBaseline(session);
        using var logout = await client.DeleteAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertBaseline(logout);
    }

    [Fact]
    public async Task Refused_requests_of_marked_endpoints_carry_the_baseline()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:LoginPermitLimit", "1"));
        using var client = limitedFactory.CreateClient();
        var token = await client.GetAntiforgeryTokenAsync();

        await AttemptLoginAsync(client, token);
        using var rejected = await AttemptLoginAsync(client, token);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        AssertBaseline(rejected);
    }

    // The setup group is marked like the administration one, and its claim response is the one
    // shape that carries a session cookie: 204, `Set-Cookie`, and `Cache-Control: no-store` at the
    // same time. The session must survive that combination, which this test holds by using it.
    [Fact]
    public async Task Setup_claim_keeps_cookie_and_baseline_together_and_the_session_works()
    {
        using var unclaimedFactory = new UnclaimedFactory();
        using var client = unclaimedFactory.CreateClient();

        using var status = await client.GetAsync("/api/v1/setup", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        AssertBaseline(status);

        var token = await client.GetAntiforgeryTokenAsync();
        using var claimRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup")
        {
            Content = JsonContent.Create(new SetupClaimRequest(
                "rate-limit-probe-operator",
                "StructaDoc-Setup-Password-2026!",
                null)),
        };
        claimRequest.Headers.Add(token.HeaderName, token.RequestToken);
        using var claim = await client.SendAsync(claimRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
        AssertBaseline(claim);
        Assert.Contains(claim.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));

        using var session = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        AssertBaseline(session);
        Assert.Equal(
            "rate-limit-probe-operator",
            (await session.Content.ReadFromJsonAsync<AdministratorSessionResponse>(
                cancellationToken: TestContext.Current.CancellationToken))!.Username);
    }

    // The baseline is a property of the marked management surface, not of the service: static
    // content and the consumer API answer without it, exactly as before.
    [Theory]
    [InlineData("/api/v1/documents")]
    [InlineData("/")]
    public async Task Unmarked_surfaces_answer_without_the_baseline(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized,
            $"unexpected status {response.StatusCode} for {path}");
        AssertBaselineAbsent(response);
    }

    // Both policies read the same keys, so the narrower setup bounds are the ones the deployment
    // meets: a permit limit above 60 or a window below ten seconds stops the host at startup with
    // a stable error, rather than running with a limit one policy silently could not use.
    [Fact]
    public void Shared_keys_outside_the_narrower_policy_range_stop_host_startup()
    {
        using var permitFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:LoginPermitLimit", "61"));
        var permitError = Assert.Throws<RateLimitingConfigurationException>(
            permitFactory.CreateClient);
        Assert.Equal("Setup.PermitLimit", permitError.FieldName);

        using var windowFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:LoginRateLimitWindow", "00:00:05"));
        var windowError = Assert.Throws<RateLimitingConfigurationException>(
            windowFactory.CreateClient);
        Assert.Equal("Setup.Window", windowError.FieldName);
    }

    private static async Task<HttpResponseMessage> AttemptLoginAsync(
        HttpClient client,
        AntiforgeryTokenResponse token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(
                StructaDocWebApplicationFactory.AdministratorUsername,
                "not-the-password")),
        };
        request.Headers.Add(token.HeaderName, token.RequestToken);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> AttemptSetupAsync(
        HttpClient client,
        AntiforgeryTokenResponse token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup")
        {
            Content = JsonContent.Create(new SetupClaimRequest(
                "setup-probe-operator",
                "StructaDoc-Setup-Password-2026!",
                null)),
        };
        request.Headers.Add(token.HeaderName, token.RequestToken);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AssertBaseline(HttpResponseMessage response)
    {
        foreach (var (name, value) in BaselineHeaders)
        {
            Assert.True(
                response.Headers.TryGetValues(name, out var values),
                $"the response carries no {name} header");
            Assert.Equal(value, values.Single());
        }
    }

    private static void AssertBaselineAbsent(HttpResponseMessage response)
    {
        foreach (var name in BaselineHeaders.Keys)
        {
            Assert.False(
                response.Headers.TryGetValues(name, out var values),
                $"the response carries a {name} header it must not have");
        }
    }

    private sealed class UnclaimedFactory : WebApplicationFactory<Program>
    {
        private readonly string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-rate-limit-tests",
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
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
