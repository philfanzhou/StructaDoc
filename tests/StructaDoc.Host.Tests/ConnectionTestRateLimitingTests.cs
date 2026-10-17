using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The three settings connection tests make the service reach an address the caller chose, so they
/// share one rate limit. These tests hold the promises that limit makes: a refusal answers with the
/// same safe problem+json body sign-in limiting produces — the rule that fired, a correlation id,
/// and nothing about the address probed, the partition key, or the caller — all three tests spend
/// one budget while every other endpoint keeps its own verdict; the bucket is counted per
/// administrator identity rather than per address; it refills once the window passes; a rejection
/// is logged rather than passing silently; and the shipped fixed limits are what actually govern.
/// </summary>
public sealed class ConnectionTestRateLimitingTests
{
    private const string ExceededTypeUri = "urn:servicemantle:error:rate_limit.exceeded";

    [Fact]
    public async Task Exhausted_connection_tests_answer_the_safe_problem_body()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = LimitedFactory(deployment, permitLimit: 3);
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var within = await PostOidcTestAsync(client);
            Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        }

        using var rejected = await PostOidcTestAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);

        var payload = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // The refusal names the rule that fired and the request it fired on, and nothing else: not
        // the authority that was being probed, not the partition the caller landed in, and nothing
        // about the session that was counted.
        Assert.DoesNotContain("issuer.example", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-test-", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-client", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("::1", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SettingsTestDeployment.Username, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SettingsTestDeployment.Password, payload, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(payload);
        Assert.Equal(ExceededTypeUri, body.RootElement.GetProperty("type").GetString());
        Assert.Equal("Too many requests.", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("rate_limit.exceeded", body.RootElement.GetProperty("errorCode").GetString());
        var correlationId = body.RootElement.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.Equal(
            rejected.Headers.GetValues("x-correlation-id").Single(),
            correlationId);
    }

    // All three tests reach outward, so they spend one budget: exhausting it through the
    // identity-provider probe refuses the storage and database probes too, while reading settings,
    // reading the session, and sign-in itself keep their own verdicts.
    [Fact]
    public async Task All_three_tests_share_one_bucket_and_no_other_endpoint_spends_it()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = LimitedFactory(deployment, permitLimit: 3);
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var within = await PostOidcTestAsync(client);
            Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        }

        using var storageTest = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/storage/test",
            new StorageConnectionTestRequest(Provider: "Local", RootPath: deployment.BusinessDatabasePath),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, storageTest.StatusCode);

        using var databaseTest = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/database/test",
            new DatabaseConnectionTestRequest(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, databaseTest.StatusCode);

        using var settings = await client.GetAsync(
            "/api/v1/admin/settings",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        using var session = await client.GetAsync(
            "/api/v1/admin/session",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        // Sign-in runs under its own policy, so the exhausted connection-test bucket cannot answer
        // for it: the wrong password still gets the sign-in verdict, not a refusal.
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(
                SettingsTestDeployment.Username,
                "not-the-password")),
        };
        using var login = await client.SendAsync(loginRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    // The bucket is counted per administrator identity, not per address: the anonymous caller
    // exhausts the address partition the unauthenticated fallback uses, and a signed-in
    // administrator from the same place still tests.
    [Fact]
    public async Task A_signed_in_caller_counts_apart_from_the_address_partition()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = LimitedFactory(deployment, permitLimit: 3);
        using var anonymous = factory.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var refused = await PostOidcTestAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var exhausted = await PostOidcTestAsync(anonymous);
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);
        using var within = await PostOidcTestAsync(client);
        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
    }

    // A sliding window replenishes: the same caller, on the same session, can test again once the
    // window has passed. The limit is not a lockout.
    [Fact]
    public async Task The_bucket_refills_once_the_window_has_passed()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = LimitedFactory(
            deployment,
            permitLimit: 2,
            window: TimeSpan.FromSeconds(1),
            segmentsPerWindow: 1);
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var within = await PostOidcTestAsync(client);
            Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        }

        using var rejected = await PostOidcTestAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);

        using var recovered = await PostOidcTestAsync(client);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    // A rejection leaves a trace an operator can find: the shared refusal handler logs a warning
    // through the console pipeline's category for rate limiting, on every policy it answers for.
    [Fact]
    public async Task A_rejection_is_logged_as_a_warning()
    {
        using var deployment = new SettingsTestDeployment();
        var recorder = new RecordingLoggerProvider();
        using var factory = LimitedFactory(deployment, permitLimit: 1, recorder: recorder);
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        using var within = await PostOidcTestAsync(client);
        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        using var rejected = await PostOidcTestAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        Assert.Contains(
            recorder.Entries,
            entry => entry.Category == "ServiceMantle.Http.RateLimiting"
                && entry.Level == LogLevel.Warning
                && entry.Message.Contains("rate-limit", StringComparison.Ordinal));
    }

    // The limits are fixed in code rather than settable, so this is the guard on the shipped
    // values: through the composition itself, with nothing overridden, twenty attempts a minute
    // are answered and the twenty-first is refused.
    [Fact]
    public async Task The_shipped_limits_govern_the_composition()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var within = await PostOidcTestAsync(client);
            Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        }

        using var rejected = await PostOidcTestAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> LimitedFactory(
        SettingsTestDeployment deployment,
        int permitLimit,
        TimeSpan? window = null,
        int segmentsPerWindow = 6,
        RecordingLoggerProvider? recorder = null)
    {
        var limits = new ConnectionTestRateLimitOptions
        {
            PermitLimit = permitLimit,
            Window = window ?? TimeSpan.FromMinutes(1),
            SegmentsPerWindow = segmentsPerWindow,
        };
        limits.Validate();
        return deployment.CreateFactory(builder => builder.ConfigureServices(services =>
        {
            // The test host applies its registrations after the Program's own, so this instance is
            // the one the partition resolves, standing in for the shipped limits.
            services.AddSingleton(limits);
            if (recorder is not null)
            {
                services.AddSingleton<ILoggerProvider>(recorder);
            }
        }));
    }

    private static Task<HttpResponseMessage> PostOidcTestAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/api/v1/admin/settings/oidc/test",
            new OidcConnectionTestRequest("http://issuer.example", RequireHttpsMetadata: true),
            cancellationToken: TestContext.Current.CancellationToken);

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    /// <summary>
    /// Records what the host's loggers write, registered next to the Serilog console bridge so the
    /// assertion reads the same events the pipeline would render.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> Entries => entries.ToList();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string category,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Information)
                {
                    entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
