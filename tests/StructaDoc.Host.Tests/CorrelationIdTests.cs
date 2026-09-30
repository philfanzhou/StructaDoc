using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Web.Http;
using ServiceMantle.Web.Logging;

namespace StructaDoc.Host.Tests;

// The Host resolves one Correlation ID per request (UseServiceMantleCorrelationId) and returns it
// on every response through x-correlation-id, so a consumer report can be matched to the exact
// request in the logs. These tests hold the published contract: valid caller values are echoed
// verbatim, everything else is replaced by a generated value, the header reaches responses
// produced by the static files, the SPA fallback, and the API error paths, and the value in the
// response header, the request context, and the downstream log scope is one and the same.
public sealed partial class CorrelationIdTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    private const string ResponseHeaderName = ServiceHeaderNames.CorrelationId;
    private const string FieldName = ServiceLogFieldNames.CorrelationId;

    public static TheoryData<string> ValidCallerValues => new()
    {
        // The full accepted charset: first character a letter or digit, then letters, digits,
        // `.`, `_`, `-`. Echoed verbatim, so mixed case and separators survive round-trip.
        "CorrelationProbe123",
        "a.b_c-d9",
        "9",
        new string('a', 64),
    };

    public static TheoryData<string?> RejectedCallerValues => new()
    {
        (string?)null,               // no header at all
        "",                          // empty
        "   ",                       // whitespace only
        " correlation-probe",        // leading whitespace
        "correlation probe",         // inner whitespace
        "correlation,probe",         // comma-joined values
        "correlation;probe",         // illegal character
        new string('a', 65),         // one character over the 64 limit
    };

    [Theory]
    [MemberData(nameof(ValidCallerValues))]
    public async Task Valid_caller_values_are_echoed_verbatim(string value)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        request.Headers.TryAddWithoutValidation(ResponseHeaderName, value);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(value, response.Headers.TryGetValues(ResponseHeaderName, out var echoed) ? echoed.Single() : null);
    }

    [Theory]
    [MemberData(nameof(RejectedCallerValues))]
    public async Task Rejected_caller_values_are_replaced_by_a_generated_value(string? value)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        if (value is not null)
        {
            request.Headers.TryAddWithoutValidation(ResponseHeaderName, value);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        AssertGeneratedCorrelationId(response);
    }

    // The boundary of the accepted length: 64 characters is the last echoed value, 65 is the first
    // rejected one. The value shape a generated ID must have, and only that, replaces the caller's.
    [Fact]
    public async Task Repeated_header_values_are_replaced_by_a_generated_value()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        // Two separately stated header lines, each a legal value on its own.
        request.Headers.TryAddWithoutValidation(ResponseHeaderName, "correlation-probe-one");
        request.Headers.TryAddWithoutValidation(ResponseHeaderName, "correlation-probe-two");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        AssertGeneratedCorrelationId(response);
    }

    [Theory]
    [InlineData("/index.html")]       // served by the static files middleware
    [InlineData("/")]                 // default files, then static files
    [InlineData("/workspace")]        // the SPA fallback answers the client-side route
    [InlineData("/api/v1/does-not-exist")]   // the API fallback guard answers 404
    [InlineData("/health/does-not-exist")]   // the health path guard answers 404
    public async Task Every_response_shape_carries_the_header(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"unexpected status {response.StatusCode} for {path}");
        AssertGeneratedCorrelationId(response);
    }

    // An API client credential is refused with 401, which is the error shape an integrator holds
    // the header contract against, not just the success shapes.
    [Fact]
    public async Task Unauthenticated_api_responses_carry_the_header()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/documents", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertGeneratedCorrelationId(response);
    }

    // The log scope assertion needs a host whose console sink binds while the capture owns the
    // output, which the shared web host cannot offer. The host below wires the same registration
    // as the Host's Program.cs — identity, Serilog pipeline, correlation middleware — around a
    // probe endpoint that logs inside a request.
    [Fact]
    public async Task Log_scope_carries_the_correlation_id_and_the_identity_fields()
    {
        var correlationValue = "CorrelationScopeProbe776";

        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        try
        {
            using var host = await StartProbeHostAsync();
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.TryAddWithoutValidation(ResponseHeaderName, correlationValue);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal(
                correlationValue,
                await response.Content.ReadFromJsonAsync<string>(
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var output = capture.Snapshot();
        // The single value is visible in the response above and in the scope of the request's own
        // log event, together with the identity fields the scope publishes beside it.
        Assert.Contains("Correlation scope probe message", output, StringComparison.Ordinal);
        Assert.Contains(FieldName, output, StringComparison.Ordinal);
        Assert.Contains(correlationValue, output, StringComparison.Ordinal);
        Assert.Contains(ServiceLogFieldNames.ServiceName, output, StringComparison.Ordinal);
        Assert.Contains(ServiceLogFieldNames.ServiceVersion, output, StringComparison.Ordinal);
        Assert.Contains(ServiceLogFieldNames.InstanceId, output, StringComparison.Ordinal);
    }

    // The rejected raw value must not survive into anything the middleware publishes: the response
    // carries the generated value, and neither the log output nor the request context exposes the
    // original.
    [Fact]
    public async Task Rejected_raw_value_reaches_no_response_or_log_output()
    {
        const string rawValue = "REJECTED-RAW-VALUE-;=";

        var originalOutput = Console.Out;
        using var capture = new ChainingConsoleWriter(originalOutput);
        Console.SetOut(capture);
        try
        {
            using var host = await StartProbeHostAsync();
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.TryAddWithoutValidation(ResponseHeaderName, rawValue);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode);
            AssertGeneratedCorrelationId(response);
            Assert.DoesNotContain(
                rawValue,
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        Assert.DoesNotContain(rawValue, capture.Snapshot(), StringComparison.Ordinal);
    }

    // The scope wraps the downstream call and is released on failure: a request that throws does
    // not leave its Correlation ID behind on the next request's events.
    [Fact]
    public async Task Failing_request_releases_the_scope()
    {
        using var host = await StartProbeHostAsync();
        using var client = host.GetTestClient();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("/probe/failure", TestContext.Current.CancellationToken));

        using var followUp = new HttpRequestMessage(HttpMethod.Get, "/probe");
        followUp.Headers.TryAddWithoutValidation(ResponseHeaderName, "CorrelationScopeFollowUp");
        using var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(
            "CorrelationScopeFollowUp",
            response.Headers.GetValues(ResponseHeaderName).Single());
    }

    // Cancellation takes the same path: the request ends without a response, and the next request
    // gets its own scope, not a leaked one from the cancelled request.
    [Fact]
    public async Task Cancelled_request_releases_the_scope()
    {
        using var host = await StartProbeHostAsync();
        using var client = host.GetTestClient();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/probe/delay", cancellation.Token));

        using var followUp = new HttpRequestMessage(HttpMethod.Get, "/probe");
        followUp.Headers.TryAddWithoutValidation(ResponseHeaderName, "CorrelationScopeAfterCancel");
        using var response = await client.SendAsync(followUp, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(
            "CorrelationScopeAfterCancel",
            response.Headers.GetValues(ResponseHeaderName).Single());
    }

    private static void AssertGeneratedCorrelationId(HttpResponseMessage response)
    {
        Assert.True(
            response.Headers.TryGetValues(ResponseHeaderName, out var values),
            "the response carries no correlation header");
        var value = Assert.Single(values);
        Assert.Matches(GeneratedCorrelationIdPattern(), value);
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex GeneratedCorrelationIdPattern();

    /// <summary>
    /// A web host with the Host's own observability wiring — identity, Serilog pipeline with the
    /// shipped level settings, and the correlation middleware — around endpoints that log while a
    /// request runs. The scope the middleware opens is what carries the fields asserted above.
    /// </summary>
    private static async Task<WebApplication> StartProbeHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("structadoc"),
            InstanceId.Parse($"structadoc-{Guid.NewGuid():N}"));
        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = LogLevel.Information;
            options.MinimumLevelOverrides = new Dictionary<string, LogLevel>
            {
                ["Microsoft.AspNetCore"] = LogLevel.Warning,
                ["Microsoft.EntityFrameworkCore.Database.Command"] = LogLevel.Warning,
            };
            options.IncludeScopes = true;
        });
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.UseServiceMantleCorrelationId();
        app.MapGet(
            "/probe",
            (HttpContext context) =>
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("StructaDoc.CorrelationScopeProbe")
                    .LogInformation("Correlation scope probe message");
                return Results.Ok(
                    context.TryGetServiceMantleCorrelationId(out var value) ? value : null);
            });
        app.MapGet(
            "/probe/failure",
            (HttpContext context) =>
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("StructaDoc.CorrelationScopeProbe")
                    .LogInformation("Correlation scope probe message");
                throw new InvalidOperationException("Correlation probe downstream failure");
            });
        app.MapGet(
            "/probe/delay",
            async (HttpContext context) => await Task.Delay(
                TimeSpan.FromSeconds(30),
                context.RequestAborted));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
