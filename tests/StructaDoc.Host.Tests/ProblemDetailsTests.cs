using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Web.Http;
using ServiceMantle.Web.Logging;

namespace StructaDoc.Host.Tests;

// Uncaught exceptions are answered by the ServiceMantle Problem Details middleware with one
// environment-independent RFC 7807 body: the fixed type, title, status, correlationId, and errorCode
// fields and nothing else. The response tests below run against a probe host that composes the
// Host's own middleware order — identity, correlation, Problem Details, then endpoints — because an
// exception has to sit downstream of the middleware to exercise it; the regression tests at the end
// run against the real Host pipeline through the shared factory and hold that responses endpoints
// produce themselves are unchanged.
public sealed class ProblemDetailsTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    private const string ThrowRoute = "/problem-details-probe/unhandled";
    private const string StandaloneCancellationRoute = "/problem-details-probe/standalone-cancellation";
    private const string CallerCancellationRoute = "/problem-details-probe/caller-cancellation";
    private const string FallbackTypeUri = "urn:servicemantle:error:http.internal_server_error";
    private const string FallbackTitle = "An unexpected error occurred.";
    private const string FallbackErrorCode = "http.internal_server_error";
    private const string UnhandledFailureMessage =
        "Problem details probe: deliberate unhandled failure with unique text QX7-PROBE-90211";

    // Pinned to Production, so this is the non-Development contract: the same fixed body the
    // shipped image answers with, not a developer exception page.
    [Fact]
    public async Task Unmapped_exception_answers_the_fixed_500_problem_body()
    {
        using var host = await StartProbeHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(ThrowRoute, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        // The one relationship the fixed fields have to the rest of the response: the body's
        // correlation ID is the value the correlation middleware already put in the header, so a
        // consumer report matches both the response and the log entry to the same request.
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            AssertFixedFallbackFields(body);
            Assert.Equal(
                response.Headers.GetValues(ServiceHeaderNames.CorrelationId).Single(),
                body.RootElement.GetProperty("correlationId").GetString());
        }

        Assert.DoesNotContain(
            UnhandledFailureMessage,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // The middleware's own log line is the safe diagnostics surface: the error code and the
    // correlation ID, never the exception message, stack, inner exceptions, or Data.
    [Fact]
    public async Task Fallback_log_names_the_error_code_and_correlation_id_but_no_exception_text()
    {
        // The console sink binds when a host starts, so the capture must own the console before
        // the probe host starts. ChainingConsoleWriter forwards to the real console, so hosts other
        // tests started keep working.
        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        string correlationId;
        try
        {
            using var host = await StartProbeHostAsync();
            using var client = host.GetTestClient();
            using var response = await client.GetAsync(ThrowRoute, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            correlationId = response.Headers.GetValues(ServiceHeaderNames.CorrelationId).Single();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var output = capture.Snapshot();
        Assert.Contains("A ServiceMantle request failed with", output, StringComparison.Ordinal);
        Assert.Contains(FallbackErrorCode, output, StringComparison.Ordinal);
        Assert.Contains(correlationId, output, StringComparison.Ordinal);
        Assert.DoesNotContain(UnhandledFailureMessage, output, StringComparison.Ordinal);
    }

    // Cancellation the caller asked for must not become a 500: the request ends without a fallback
    // body, which for the client is the cancellation it caused.
    [Fact]
    public async Task Caller_requested_cancellation_propagates_without_a_500_body()
    {
        using var host = await StartProbeHostAsync();
        using var client = host.GetTestClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync(CallerCancellationRoute, cancellation.Token));
    }

    // A cancellation that is thrown as an exception rather than requested by the caller is not a
    // cancellation anyone is waiting for, so the library contract treats it like any other unmapped
    // exception: the same fixed 500 fallback body.
    [Fact]
    public async Task Independently_thrown_cancellation_is_answered_like_any_unmapped_exception()
    {
        using var host = await StartProbeHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(
            StandaloneCancellationRoute,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertFixedFallbackFields(body);
    }

    // The fallback only exists for exceptions nothing else handled. Responses the real Host and its
    // endpoints already produce keep their existing contracts through the shared factory: the API
    // fallback's own 404 shape, and an endpoint's self-produced antiforgery 400, both distinct from
    // the 500 fallback body.
    [Fact]
    public async Task Self_produced_problem_responses_keep_their_existing_shapes()
    {
        using var client = factory.CreateClient();

        using var missing = await client.GetAsync(
            "/api/v1/does-not-exist",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        using (var body = JsonDocument.Parse(
            await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Endpoint not found", body.RootElement.GetProperty("title").GetString());
            Assert.NotEqual(FallbackTypeUri, body.RootElement.GetProperty("type").GetString());
        }

        // A well-formed login request without an antiforgery token reaches the endpoint's own
        // antiforgery guard, which answers with its existing 400 problem body.
        using var unguarded = await client.PostAsJsonAsync(
            "/api/v1/admin/session",
            new { username = "nobody", password = "no-password" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unguarded.StatusCode);
        Assert.Equal("application/problem+json", unguarded.Content.Headers.ContentType?.MediaType);
        using (var body = JsonDocument.Parse(
            await unguarded.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Antiforgery validation failed", body.RootElement.GetProperty("title").GetString());
            Assert.NotEqual(FallbackTypeUri, body.RootElement.GetProperty("type").GetString());
        }
    }

    private static void AssertFixedFallbackFields(JsonDocument body)
    {
        Assert.Equal(FallbackTypeUri, body.RootElement.GetProperty("type").GetString());
        Assert.Equal(FallbackTitle, body.RootElement.GetProperty("title").GetString());
        Assert.Equal(500, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(FallbackErrorCode, body.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
    }

    /// <summary>
    /// A web host with the Host's own wiring — ServiceMantle identity, the Serilog console pipeline
    /// with the shipped level settings, the correlation middleware, and the Problem Details
    /// middleware — around probe endpoints that fail the ways the fallback contract names. The
    /// environment is pinned to Production because the fallback contract is the non-Development
    /// one: Development used to answer with the developer exception page, and the shipped behavior
    /// is now the same safe body everywhere.
    /// </summary>
    private static async Task<WebApplication> StartProbeHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Services.AddServiceMantle(
            ServiceId.Parse("structadoc"),
            InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"));
        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = LogLevel.Information;
            options.MinimumLevelOverrides = new Dictionary<string, LogLevel>
            {
                ["Microsoft.AspNetCore"] = LogLevel.Warning,
            };
            options.IncludeScopes = true;
        });
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.UseServiceMantleCorrelationId();
        app.UseServiceMantleProblemDetails();
        app.MapGet(
            ThrowRoute,
            () =>
            {
                throw new InvalidOperationException(UnhandledFailureMessage);
            });
        app.MapGet(
            StandaloneCancellationRoute,
            () =>
            {
                throw new OperationCanceledException(
                    "Problem details probe: standalone cancellation with unique text QX7-CANCEL-4471");
            });
        app.MapGet(
            CallerCancellationRoute,
            async (HttpContext context) => await Task.Delay(
                TimeSpan.FromSeconds(30),
                context.RequestAborted));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
