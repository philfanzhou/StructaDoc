using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Providers;
using StructaDoc.Contracts.Recognition;
using StructaDoc.Host.Recognition;
using static StructaDoc.Host.Tests.LayoutDetectEndpointTests;

namespace StructaDoc.Host.Tests;

// The public non-archiving image text-recognition endpoint (ADR-0015), against the real host and
// the real stores. What these tests hold is the issue's acceptance surface for the route: the
// documented response shape, "persists nothing" end to end, the off-by-default setting, the
// configuration error when the cloud Provider cannot serve, the stable failure mapping, the
// API-client-only scope that neither grants nor is granted by `layout:detect`, the shared input
// rules, and the overload cap. The cloud behind the exchange is a stub provider registered over
// the adapter, so no request leaves the process.
public sealed class ImageRecognizeEndpointTests
{
    private const string Route = "/api/v1/image/recognize";

    [Fact]
    public async Task A_supported_image_returns_blocks_with_lines_and_persists_nothing()
    {
        using var factory = new RecognitionFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.Host.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(1240, 1754), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GetHeader(response, "x-correlation-id"), body.GetProperty("requestId").GetString());
        Assert.Equal(1240, body.GetProperty("width").GetInt32());
        Assert.Equal(1754, body.GetProperty("height").GetInt32());
        Assert.Equal(
            ImageRecognizeResponse.CanonicalCoordinateFrame,
            body.GetProperty("coordinateFrame").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("orientation").ValueKind);
        Assert.Equal("mineru-cloud", body.GetProperty("engine").GetString());
        Assert.Equal("vlm", body.GetProperty("model").GetString());
        Assert.Equal("3.4.4", body.GetProperty("version").GetString());

        // Blocks in reading order with a contiguous sequence, Canonical Model types and formats,
        // an object bbox, and lines for the text-bearing block.
        var blocks = body.GetProperty("blocks").EnumerateArray().ToArray();
        Assert.Equal([0, 1], blocks.Select(block => block.GetProperty("sequence").GetInt32()).ToArray());
        var rows = blocks[0];
        Assert.Equal("text", rows.GetProperty("type").GetString());
        Assert.Equal("apple n. 苹果\nborrow v. 借", rows.GetProperty("content").GetString());
        Assert.Equal("plain", rows.GetProperty("contentFormat").GetString());
        Assert.Equal(0.1, rows.GetProperty("bbox").GetProperty("x0").GetDouble());
        Assert.Equal(0.3, rows.GetProperty("bbox").GetProperty("y1").GetDouble());
        var lines = rows.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(["apple n. 苹果", "borrow v. 借"], lines.Select(line => line.GetProperty("text").GetString()).ToArray());
        Assert.Equal(0.2, lines[1].GetProperty("bbox").GetProperty("y0").GetDouble());

        var table = blocks[1];
        Assert.Equal("table", table.GetProperty("type").GetString());
        Assert.Equal("html", table.GetProperty("contentFormat").GetString());
        Assert.Equal("Synthetic caption", table.GetProperty("caption").GetString());
        Assert.Equal(0, table.GetProperty("lines").GetArrayLength());

        // Nothing addressable rides along: no resource IDs, no download URLs.
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"id\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("http", raw, StringComparison.OrdinalIgnoreCase);

        await AssertNothingPersistedAsync(factory.Host, countsBefore);
        Assert.Empty(factory.Stub.TemporaryFilesSeenDuringExecution);
    }

    [Fact]
    public async Task An_empty_page_is_a_documented_success_with_no_blocks()
    {
        using var factory = new RecognitionFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(new ImageRecognitionResult([], 100, 100));
        using var client = factory.Host.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public async Task A_rotated_jpeg_reports_its_orientation_and_hands_the_inspection_to_the_backend()
    {
        using var factory = new RecognitionFactory();
        LayoutDetectionRequest? seen = null;
        factory.Stub.Handler = (request, _) =>
        {
            seen = request;
            return Task.FromResult(new ImageRecognitionResult([], 1754, 1240));
        };
        using var client = factory.Host.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreateJpeg(1754, 1240, orientation: 6), "page.jpg", "image/jpeg"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6, body.GetProperty("orientation").GetInt32());
        Assert.NotNull(seen);
        Assert.Equal(1754, seen.StoredWidth);
        Assert.Equal(1240, seen.StoredHeight);
        Assert.Equal(6, seen.ExifOrientation);
        Assert.Equal("image/jpeg", seen.MediaType);
    }

    [Fact]
    public async Task The_mode_is_off_by_default_and_answers_unsupported_capability()
    {
        // The shipped state: Recognition:Enabled is false, so no implementation is registered and
        // nothing — not the cloud Provider, not layout detection, not document parsing — is tried.
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        var apiKey = await CreateApiClientAsync(factory, AuthenticationScopes.ImageRecognize);
        var countsBefore = await TakeCountsAsync(factory);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("recognition-capability-unsupported", body.GetProperty("code").GetString());
        Assert.Contains("Recognition:Enabled", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await AssertNothingPersistedAsync(factory, countsBefore);
    }

    [Fact]
    public async Task On_without_a_usable_default_cloud_provider_answers_the_specific_configuration_error()
    {
        // The real registration and the real configuration source against the seeded deployment:
        // the official Provider is enabled and default but has no credential, so the cloud adapter
        // refuses before sending anything, naming what is missing.
        using var inner = new StructaDocWebApplicationFactory();
        using var factory = inner.WithWebHostBuilder(builder =>
            builder.UseSetting("Recognition:Enabled", "true"));
        using var client = factory.CreateClient();
        var apiKey = await CreateApiClientAsync(factory, AuthenticationScopes.ImageRecognize);
        var countsBefore = await TakeCountsAsync(factory);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("recognition-capability-unsupported", body.GetProperty("code").GetString());
        Assert.Contains("credential", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await AssertNothingPersistedAsync(factory, countsBefore);
    }

    [Theory]
    [InlineData("mineru-recognition-timeout", ProviderFailureCategory.Transient, HttpStatusCode.GatewayTimeout, "recognition-timeout")]
    [InlineData("mineru-recognition-cloud-status-network-error", ProviderFailureCategory.Transient, HttpStatusCode.BadGateway, "recognition-backend-unavailable")]
    [InlineData("mineru-recognition-cloud-layout-response-invalid", ProviderFailureCategory.Permanent, HttpStatusCode.BadGateway, "recognition-output-invalid")]
    [InlineData("mineru-recognition-cloud-task-failed", ProviderFailureCategory.Permanent, HttpStatusCode.BadGateway, "recognition-output-invalid")]
    [InlineData("mineru-recognition-cloud-submit-rejected", ProviderFailureCategory.Permanent, HttpStatusCode.BadGateway, "recognition-output-invalid")]
    [InlineData("mineru-recognition-cloud-status-http-401", ProviderFailureCategory.Configuration, HttpStatusCode.ServiceUnavailable, "recognition-backend-unavailable")]
    [InlineData("mineru-recognition-cloud-submit-http-400", ProviderFailureCategory.Input, HttpStatusCode.BadGateway, "recognition-backend-rejected")]
    [InlineData("mineru-recognition-cloud-result-destination-denied", ProviderFailureCategory.Security, HttpStatusCode.ServiceUnavailable, "recognition-backend-unavailable")]
    public async Task Every_provider_failure_maps_to_its_stable_http_outcome(
        string errorCode,
        ProviderFailureCategory category,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var factory = new RecognitionFactory();
        factory.Stub.Handler = (_, _) => throw new ProviderException(
            errorCode,
            "A bounded, sanitized failure message.",
            category);
        using var client = factory.Host.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);
        var countsBefore = await TakeCountsAsync(factory.Host);

        using var response = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        await AssertProblemCodeAsync(response, expectedCode);
        await AssertNothingPersistedAsync(factory.Host, countsBefore);
    }

    [Fact]
    public async Task The_input_rules_are_the_layout_endpoints_under_the_recognition_codes()
    {
        using var factory = new RecognitionFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.Host.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);

        using var notMultipart = await client.PostWithApiKeyAsync(
            Route,
            new StringContent("not an upload"),
            apiKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);
        await AssertProblemCodeAsync(notMultipart, "recognition-request-invalid");

        using var twoFiles = await client.PostWithApiKeyAsync(
            Route,
            CreateShapedUpload("two-files", CreatePng(100, 100)),
            apiKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, twoFiles.StatusCode);
        await AssertProblemCodeAsync(twoFiles, "recognition-request-invalid");

        using var notAnImage = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload("GIF89a synthetic"u8.ToArray(), "page.gif", "image/gif"),
            apiKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notAnImage.StatusCode);
        await AssertProblemCodeAsync(notAnImage, "recognition-media-type-unsupported");

        using var tooManyPixels = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(32001, 10), "page.png", "image/png"),
            apiKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooManyPixels.StatusCode);
        await AssertProblemCodeAsync(tooManyPixels, "recognition-input-too-large");

        // None of the refused requests reached the backend.
        Assert.Equal(0, factory.Stub.RequestCount);
    }

    [Fact]
    public async Task The_in_flight_cap_rejects_excess_requests_before_execution()
    {
        using var factory = new RecognitionFactory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Stub.Handler = async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return SuccessResult();
        };
        using var client = factory.Host.CreateClient();
        var apiKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);

        var requests = Enumerable
            .Range(0, ImageRecognizeInFlightGate.Capacity + 1)
            .Select(_ => client.PostWithApiKeyAsync(
                Route,
                CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
                apiKey,
                TestContext.Current.CancellationToken))
            .ToArray();

        // Every accepted execution is held on the release, so the one completed request is the
        // rejected one, observed while every slot was still taken.
        await WaitForAsync(
            () => factory.Stub.PeakInFlight >= ImageRecognizeInFlightGate.Capacity
                && requests.Count(request => request.IsCompleted) == 1,
            TimeSpan.FromSeconds(30));
        release.TrySetResult();
        var responses = await Task.WhenAll(requests);

        var rejected = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("1", GetHeader(rejected, "Retry-After"));
        await AssertProblemCodeAsync(rejected, "recognition-overloaded");
        Assert.Equal(ImageRecognizeInFlightGate.Capacity, factory.Stub.RequestCount);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task The_scope_grants_the_endpoint_and_nothing_else_does()
    {
        using var factory = new RecognitionFactory();
        factory.Stub.Handler = (_, _) => Task.FromResult(SuccessResult());
        using var client = factory.Host.CreateClient();

        using var anonymous = await client.PostAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // An interactive administrator session is refused: the policy has no session branch.
        await client.LoginAsAdministratorAsync();
        using var interactive = await client.PostAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, interactive.StatusCode);

        // `layout:detect` does not grant recognition, and neither does every other scope at once.
        var layoutKey = await factory.CreateApiClientAsync(AuthenticationScopes.LayoutDetect);
        using var layoutOnly = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            layoutKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, layoutOnly.StatusCode);

        var broadKey = await factory.CreateApiClientAsync(string.Join(
            ',',
            AuthenticationScopes.DocumentsRead,
            AuthenticationScopes.DocumentsWrite,
            AuthenticationScopes.ParsesRead,
            AuthenticationScopes.ParsesWrite,
            AuthenticationScopes.LayoutDetect));
        using var broad = await client.PostWithApiKeyAsync(
            Route,
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            broadKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, broad.StatusCode);

        // And `image:recognize` does not grant layout detection.
        var recognitionKey = await factory.CreateApiClientAsync(AuthenticationScopes.ImageRecognize);
        using var layoutRoute = await client.PostWithApiKeyAsync(
            "/api/v1/layout/detect",
            CreateUpload(CreatePng(100, 100), "page.png", "image/png"),
            recognitionKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, layoutRoute.StatusCode);

        Assert.Equal(0, factory.Stub.RequestCount);
    }

    internal static ImageRecognitionResult SuccessResult() => new(
        [
            new ImageRecognitionBlock(
                "text",
                "apple n. 苹果\nborrow v. 借",
                "plain",
                new ImageRecognitionBox(0.1, 0.1, 0.9, 0.3),
                [
                    new ImageRecognitionLine("apple n. 苹果", new ImageRecognitionBox(0.1, 0.1, 0.9, 0.18)),
                    new ImageRecognitionLine("borrow v. 借", new ImageRecognitionBox(0.1, 0.2, 0.9, 0.3)),
                ]),
            new ImageRecognitionBlock(
                "table",
                "<table><tr><td>apple</td></tr></table>",
                "html",
                new ImageRecognitionBox(0.1, 0.4, 0.9, 0.8),
                [],
                caption: "Synthetic caption"),
        ],
        1240,
        1754,
        "mineru-cloud",
        "vlm",
        "3.4.4");

    /// <summary>
    /// The test host with recognition on: the registration path is the real one (bound
    /// <c>Recognition</c> section, adapter extension, resolver), with the one implementation
    /// replaced so no request leaves the process.
    /// </summary>
    internal sealed class RecognitionFactory : IDisposable
    {
        private readonly StructaDocWebApplicationFactory factory = new();

        public RecognitionFactory()
        {
            Host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Recognition:Enabled", "true");
                builder.ConfigureServices(services =>
                {
                    foreach (var descriptor in services
                                 .Where(descriptor => descriptor.ServiceType == typeof(IImageRecognitionProvider))
                                 .ToArray())
                    {
                        services.Remove(descriptor);
                    }

                    services.AddSingleton<IImageRecognitionProvider>(Stub);
                });
            });
        }

        public StubRecognitionProvider Stub { get; } = new();

        public WebApplicationFactory<Program> Host { get; }

        public async Task<IssuedApiKey> CreateApiClientAsync(string scopes) =>
            await LayoutDetectEndpointTests.CreateApiClientAsync(Host, scopes);

        public void Dispose() => factory.Dispose();
    }

    /// <summary>
    /// The instrumented recognition provider: it counts requests, tracks the peak in flight, and
    /// records the process temporary directory while executing, for the no-temporary-files proof.
    /// </summary>
    internal sealed class StubRecognitionProvider : IImageRecognitionProvider
    {
        private int inFlight;
        private int requestCount;
        private int peakInFlight;

        public Func<LayoutDetectionRequest, CancellationToken, Task<ImageRecognitionResult>>? Handler { get; set; }

        public int PeakInFlight => Volatile.Read(ref peakInFlight);

        public int RequestCount => Volatile.Read(ref requestCount);

        public List<string> TemporaryFilesSeenDuringExecution { get; } = [];

        public async Task<ImageRecognitionResult> RecognizeAsync(
            LayoutDetectionRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref requestCount);
            var current = Interlocked.Increment(ref inFlight);
            int peak;
            do
            {
                peak = Volatile.Read(ref peakInFlight);
                if (current <= peak)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref peakInFlight, current, peak) != peak);

            lock (TemporaryFilesSeenDuringExecution)
            {
                TemporaryFilesSeenDuringExecution.AddRange(
                    Directory.GetFiles(Path.GetTempPath(), "ASPNETBUF_*"));
            }

            try
            {
                if (Handler is null)
                {
                    throw new InvalidOperationException("The stub provider has no handler configured.");
                }

                return await Handler(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }
    }
}
