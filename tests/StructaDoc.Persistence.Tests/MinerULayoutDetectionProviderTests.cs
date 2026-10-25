using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Providers;
using StructaDoc.Application.Providers;

namespace StructaDoc.Persistence.Tests;

public sealed partial class MinerULayoutDetectionProviderTests
{
    private const string Route = "https://runtime.example/prefix/v1/layout/detect";

    [Fact]
    public async Task Detect_posts_the_image_to_the_single_layout_route_and_maps_the_result()
    {
        var image = "page-image-bytes"u8.ToArray();
        string? requestMediaType = null;
        byte[]? requestImage = null;
        var runtime = new LayoutOnlyRuntimeStub(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = payload.RootElement;
            requestMediaType = root.GetProperty("mediaType").GetString();
            requestImage = Convert.FromBase64String(root.GetProperty("image").GetString()!);
            return SuccessResponse();
        });
        var provider = ConfiguredProvider(runtime);

        var result = await provider.DetectLayoutAsync(
            new LayoutDetectionRequest(image, "image/png"),
            TestContext.Current.CancellationToken);

        // One request answered the whole execution: no submission, no polling, no second route.
        Assert.Equal(1, runtime.RequestCount);
        Assert.Equal("image/png", requestMediaType);
        Assert.Equal(image, requestImage);
        Assert.Equal(2480, result.SourceImageWidth);
        Assert.Equal(3508, result.SourceImageHeight);
        Assert.Equal("mineru", result.Engine);
        Assert.Equal("MinerU2.0-Layout-Detect", result.Model);
        Assert.Equal("2.0.7", result.Version);
        Assert.Empty(result.Diagnostics);
        Assert.Collection(
            result.Regions,
            region =>
            {
                // The label is normalized to the generic spelling the contract promises.
                Assert.Equal("title", region.Label);
                Assert.Equal(0.125, region.X0);
                Assert.Equal(0.240, region.Y0);
                Assert.Equal(0.875, region.X1);
                Assert.Equal(0.420, region.Y1);
            },
            region =>
            {
                Assert.Equal("table", region.Label);
                Assert.Equal(0.1, region.X0);
                Assert.Equal(0.5, region.Y0);
                Assert.Equal(0.95, region.X1);
                Assert.Equal(0.9, region.Y1);
            });
    }

    [Theory]
    [InlineData("runtime-credential", "Bearer", "runtime-credential")]
    [InlineData(null, null, null)]
    public async Task Detect_sends_the_bearer_credential_only_when_one_is_configured(
        string? configuredCredential,
        string? expectedScheme,
        string? expectedParameter)
    {
        var runtime = new LayoutOnlyRuntimeStub(_ => Task.FromResult(SuccessResponse()));
        var provider = ConfiguredProvider(runtime, credential: configuredCredential);

        await provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/jpeg"),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedScheme, runtime.LastAuthorizationScheme);
        Assert.Equal(expectedParameter, runtime.LastAuthorizationParameter);
    }

    [Fact]
    public async Task Missing_regions_is_rejected_rather_than_answering_an_empty_success()
    {
        var runtime = new LayoutOnlyRuntimeStub(_ => Task.FromResult(JsonResponse(
            """{"width":2480,"height":3508}""")));
        var provider = ConfiguredProvider(runtime);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-response-invalid", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
        Assert.False(exception.Retryable);
        Assert.Contains("'regions' is missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"width":2480,"height":3508,"regions":{}}""")]
    [InlineData("""{"width":0,"height":3508,"regions":[]}""")]
    [InlineData("""{"width":2480,"height":"3508","regions":[]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[],"engine":7}""")]
    [InlineData("""{"regions":[]}""")]
    [InlineData("""[{"label":"text"}]""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"label":"text","x0":0,"y0":0,"x1":1,"y1":1,"score":0.9}]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"label":"text","x0":0,"y0":0,"x1":0.5,"y1":0.5,"x0":0.1}]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"x0":0,"y0":0,"x1":1,"y1":1}]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"label":"text"}]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":["text"]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"label":"text","x0":0,"y0":0,"x1":"1","y1":1}]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"label":"text","x0":0,"y0":0,"x1":1.5,"y1":1}]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"label":"text","x0":0.8,"y0":0,"x1":0.5,"y1":1}]}""")]
    [InlineData("""{"width":2480,"height":3508,"regions":[{"label":"title of something long enough to be a sentence","x0":0,"y0":0,"x1":1,"y1":1}]}""")]
    public async Task Malformed_runtime_output_is_rejected_with_bounded_diagnostics(string body)
    {
        var runtime = new LayoutOnlyRuntimeStub(_ => Task.FromResult(JsonResponse(body)));
        var provider = ConfiguredProvider(runtime);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-response-invalid", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
        // The diagnostic is this service's own words at a bounded length, never the raw body.
        Assert.True(exception.Message.Length <= 2048, exception.Message);
    }

    [Fact]
    public async Task Oversized_runtime_output_is_rejected()
    {
        var oversized =
            "{\"width\":1,\"height\":1,\"regions\":[],\"engine\":\""
            + new string('x', 2 * 1024 * 1024)
            + "\"}";
        var runtime = new LayoutOnlyRuntimeStub(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
            }));
        var provider = ConfiguredProvider(runtime);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-response-too-large", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "mineru-layout-http-401", ProviderFailureCategory.Configuration)]
    [InlineData(HttpStatusCode.BadRequest, "mineru-layout-http-400", ProviderFailureCategory.Input)]
    [InlineData(HttpStatusCode.InternalServerError, "mineru-layout-http-500", ProviderFailureCategory.Transient)]
    public async Task Runtime_http_failures_map_to_the_provider_categories(
        HttpStatusCode status,
        string expectedCode,
        ProviderFailureCategory expectedCategory)
    {
        var runtime = new LayoutOnlyRuntimeStub(
            _ => Task.FromResult(JsonResponse(status, """{"detail":"unavailable"}""")));
        var provider = ConfiguredProvider(runtime);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(expectedCategory, exception.Category);
    }

    [Fact]
    public async Task An_expired_deadline_reports_a_transient_timeout()
    {
        var runtime = new LayoutOnlyRuntimeStub(
            _ => Task.FromResult(SuccessResponse()))
        {
            DelayResponse = true,
        };
        // The configured bound is the client's own deadline, so an expired one surfaces as the
        // transient timeout failure rather than the caller's cancellation.
        using var client = new HttpClient(runtime) { Timeout = TimeSpan.FromMilliseconds(200) };
        var provider = new MinerULayoutDetectionProvider(client, ConfiguredRuntime());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-timeout", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Transient, exception.Category);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_frees_the_execution_slot()
    {
        var runtime = new LayoutOnlyRuntimeStub(
            request => Task.FromResult(SuccessResponse()))
        {
            DelayResponse = true,
        };
        var provider = ConfiguredProvider(runtime);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            cancellation.Token));

        // The cancelled execution released its concurrency slot: the next call runs immediately.
        runtime.DelayResponse = false;
        var result = await provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken);
        Assert.Equal(2480, result.SourceImageWidth);
        Assert.Equal(2, runtime.RequestCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Concurrent_executions_are_bounded_by_the_configured_limit(
        int configuredConcurrency,
        int expectedPeakInFlight)
    {
        var runtime = new LayoutOnlyRuntimeStub(_ => Task.FromResult(SuccessResponse()))
        {
            ResponseDelay = TimeSpan.FromMilliseconds(200),
        };
        var provider = ConfiguredProvider(runtime, maxConcurrency: configuredConcurrency);

        // One more execution than the limit: the bound, not the arrival count, decides how many
        // are in flight at once.
        var executions = Enumerable.Range(0, expectedPeakInFlight + 1)
            .Select(_ => provider.DetectLayoutAsync(
                new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
                TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(executions);

        Assert.Equal(expectedPeakInFlight + 1, runtime.RequestCount);
        Assert.Equal(expectedPeakInFlight, runtime.PeakInFlight);
    }

    [Fact]
    public async Task An_explicitly_empty_region_list_is_a_valid_answer()
    {
        // A blank page is a real page: the runtime says it found nothing, and that answer is
        // carried as an empty region list — distinct from output that never stated a list.
        var runtime = new LayoutOnlyRuntimeStub(_ => Task.FromResult(JsonResponse(
            """{"width":100,"height":100,"regions":[]}""")));
        var provider = ConfiguredProvider(runtime);

        var result = await provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Regions);
        Assert.Equal(100, result.SourceImageWidth);
        Assert.Equal(100, result.SourceImageHeight);
    }

    [Fact]
    public async Task Executing_without_a_configured_runtime_is_an_unsupported_capability()
    {
        // The adapter itself refuses an unset runtime, so the error cannot be bypassed by
        // constructing the adapter directly.
        var provider = new MinerULayoutDetectionProvider(
            new HttpClient(new LayoutOnlyRuntimeStub(_ => Task.FromResult(SuccessResponse()))),
            LayoutRuntimeConfiguration.Unset);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("img"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
            exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Configuration, exception.Category);
    }

    [Fact]
    public void Capability_resolution_reports_an_unsupported_backend_explicitly()
    {
        var unregistered = new LayoutDetectionProviderResolver([]);
        var implementation = new MinerULayoutDetectionProvider(
            new HttpClient(new LayoutOnlyRuntimeStub(_ => Task.FromResult(SuccessResponse()))),
            ConfiguredRuntime());

        // No runtime configured: the explicit unsupported error, never a fallback provider.
        var unset = Assert.Throws<ProviderException>(
            () => unregistered.Resolve(LayoutBackendSelection.ForRuntime(LayoutRuntimeConfiguration.Unset)));
        Assert.Equal(
            ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
            unset.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Configuration, unset.Category);

        // A runtime address with no registered implementation behind it fails the same way.
        var unimplemented = Assert.Throws<ProviderException>(
            () => unregistered.Resolve(LayoutBackendSelection.ForRuntime(ConfiguredRuntime())));
        Assert.Equal(
            ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
            unimplemented.ErrorCode);

        // The cloud backend selected with no cloud implementation registered fails the same way:
        // selection is explicit and nothing guesses a different backend instead.
        var noCloud = Assert.Throws<ProviderException>(
            () => unregistered.Resolve(LayoutBackendSelection.ForCloud()));
        Assert.Equal(
            ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
            noCloud.ErrorCode);
        Assert.Contains("cloud", noCloud.Message, StringComparison.Ordinal);

        // A configured runtime with its implementation resolves to that implementation.
        var resolver = new LayoutDetectionProviderResolver([implementation]);
        Assert.Same(
            implementation,
            resolver.Resolve(LayoutBackendSelection.ForRuntime(ConfiguredRuntime())));

        // The resolution contract is explicit about its inputs: the runtime backend requires its
        // configuration and the cloud backend takes none.
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!));
        Assert.Throws<ArgumentException>(
            () => new LayoutBackendSelection(LayoutBackend.LayoutRuntime, null));
        Assert.Throws<ArgumentException>(
            () => new LayoutBackendSelection(LayoutBackend.MinerUCloud, ConfiguredRuntime()));
        Assert.Throws<InvalidOperationException>(
            () => new LayoutDetectionProviderResolver([implementation, implementation]));
    }

    [Fact]
    public void Registration_without_a_runtime_configures_no_layout_implementation()
    {
        using var services = new ServiceCollection()
            .AddStructaDocLayoutDetection(new LayoutRuntimeOptions(), new LayoutOptions())
            .BuildServiceProvider();

        var resolver = services.GetRequiredService<ILayoutDetectionProviderResolver>();
        Assert.Empty(services.GetServices<ILayoutDetectionProvider>());
        var exception = Assert.Throws<ProviderException>(
            () => resolver.Resolve(LayoutBackendSelection.ForRuntime(ConfiguredRuntime())));
        Assert.Equal(
            ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
            exception.ErrorCode);
    }

    [Fact]
    public void Registration_with_a_runtime_resolves_the_layout_adapter()
    {
        using var services = new ServiceCollection()
            .AddStructaDocLayoutDetection(new LayoutRuntimeOptions
            {
                BaseUrl = "https://runtime.example/prefix",
                Credential = "runtime-credential",
                Timeout = TimeSpan.FromSeconds(45),
                MaxConcurrency = 3,
            }, new LayoutOptions())
            .BuildServiceProvider();

        var resolver = services.GetRequiredService<ILayoutDetectionProviderResolver>();
        var implementation = resolver.Resolve(LayoutBackendSelection.ForRuntime(ConfiguredRuntime()));
        var adapter = Assert.IsType<MinerULayoutDetectionProvider>(implementation);
        Assert.Single(services.GetServices<ILayoutDetectionProvider>());
    }

    [Fact]
    public void Registration_publishes_the_bound_configuration_for_the_endpoint()
    {
        // The endpoint resolves per request against the configuration the registration bound, so
        // both branches — runtime configured and the shipped unset state — have to publish it:
        // what the endpoint resolves with is exactly what was bound here.
        using var configured = new ServiceCollection()
            .AddStructaDocLayoutDetection(new LayoutRuntimeOptions
            {
                BaseUrl = "https://runtime.example/prefix",
                Timeout = TimeSpan.FromSeconds(45),
                MaxConcurrency = 3,
            }, new LayoutOptions())
            .BuildServiceProvider();
        var published = configured.GetRequiredService<LayoutRuntimeConfiguration>();
        Assert.Equal(new Uri("https://runtime.example/prefix"), published.BaseUri);
        Assert.Equal(TimeSpan.FromSeconds(45), published.RequestTimeout);
        Assert.Equal(3, published.MaxConcurrency);
        var selection = configured.GetRequiredService<LayoutBackendSelection>();
        Assert.Equal(LayoutBackend.LayoutRuntime, selection.Backend);
        Assert.Same(published, selection.RuntimeConfiguration);

        using var unset = new ServiceCollection()
            .AddStructaDocLayoutDetection(new LayoutRuntimeOptions(), new LayoutOptions())
            .BuildServiceProvider();
        Assert.Null(unset.GetRequiredService<LayoutRuntimeConfiguration>().BaseUri);
        Assert.Equal(
            LayoutBackend.LayoutRuntime,
            unset.GetRequiredService<LayoutBackendSelection>().Backend);
    }

    [Fact]
    public void Request_records_reject_anything_but_a_bounded_jpeg_or_png_image()
    {
        Assert.Throws<ArgumentException>(
            () => new LayoutDetectionRequest(ReadOnlyMemory<byte>.Empty, "image/png"));
        Assert.Throws<ArgumentException>(
            () => new LayoutDetectionRequest("img"u8.ToArray(), "application/pdf"));
        Assert.Throws<ArgumentException>(
            () => new LayoutDetectionRequest("img"u8.ToArray(), "image/tiff"));

        var oversized = new byte[LayoutDetectionRequest.MaximumImageBytes + 1];
        Assert.Throws<ArgumentException>(() => new LayoutDetectionRequest(oversized, "image/png"));

        // The two supported spellings normalize to one, so the wire protocol carries one value.
        Assert.Equal(
            "image/png",
            new LayoutDetectionRequest("img"u8.ToArray(), "IMAGE/PNG").MediaType);
    }

    [Fact]
    public void Region_records_enforce_the_canonical_box_contract()
    {
        Assert.Equal(
            "title",
            new LayoutDetectionRegion("Title", 0, 0, 1, 1).Label);
        Assert.Throws<ArgumentException>(
            () => new LayoutDetectionRegion("text", 0.8, 0, 0.5, 1));
        Assert.Throws<ArgumentException>(
            () => new LayoutDetectionRegion("text", 0, 0, 1.01, 1));
        Assert.Throws<ArgumentException>(
            () => new LayoutDetectionRegion("text", -0.1, 0, 1, 1));
        Assert.Throws<ArgumentException>(
            () => new LayoutDetectionRegion("a label with spaces", 0, 0, 1, 1));
    }

    [Fact]
    public void Result_diagnostics_enforce_the_bounded_slot()
    {
        // The diagnostics slot is what the public response serializes, so its bound is a contract
        // guarantee rather than an internal nicety: one note at the exact bound is the last
        // accepted length, one past it is refused, and so is any blank note.
        var atBound = new LayoutDetectionResult(
            [],
            100,
            100,
            diagnostics: [new string('n', LayoutDetectionResult.MaximumDiagnosticLength)]);
        Assert.Equal(
            [new string('n', LayoutDetectionResult.MaximumDiagnosticLength)],
            atBound.Diagnostics);

        Assert.Throws<ArgumentException>(() => new LayoutDetectionResult(
            [],
            100,
            100,
            diagnostics: [new string('n', LayoutDetectionResult.MaximumDiagnosticLength + 1)]));
        Assert.Throws<ArgumentException>(() => new LayoutDetectionResult(
            [],
            100,
            100,
            diagnostics: ["valid note", " "]));
        Assert.Throws<ArgumentException>(() => new LayoutDetectionResult(
            [],
            100,
            100,
            diagnostics: Enumerable
                .Range(0, LayoutDetectionResult.MaximumDiagnosticCount + 1)
                .Select(index => $"note {index}")
                .ToArray()));
    }

    private static LayoutRuntimeConfiguration ConfiguredRuntime(
        string? credential = null,
        int maxConcurrency = 1) => new(
        new Uri("https://runtime.example/prefix/"),
        credential is null ? null : new ProviderCredential(credential),
        TimeSpan.FromSeconds(30),
        maxConcurrency);

    private static MinerULayoutDetectionProvider ConfiguredProvider(
        LayoutOnlyRuntimeStub runtime,
        string? credential = null,
        int maxConcurrency = 1) => new(
        new HttpClient(runtime),
        ConfiguredRuntime(credential, maxConcurrency));

    private static HttpResponseMessage SuccessResponse() => JsonResponse(
        """
        {"width":2480,"height":3508,"engine":"mineru","model":"MinerU2.0-Layout-Detect","version":"2.0.7",
         "regions":[
           {"label":"Title","x0":0.125,"y0":0.240,"x1":0.875,"y1":0.420},
           {"label":"table","x0":0.1,"y0":0.5,"x1":0.95,"y1":0.9}
         ]}
        """);

    private static HttpResponseMessage JsonResponse(string json) => JsonResponse(
        HttpStatusCode.OK,
        json);

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(
        statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// An instrumented layout runtime: every request is checked against the single-route wire
    /// contract before it is answered. A full-parsing submission, a task poll, a content
    /// recognition call, or any request to any other path fails the test here — the check is on
    /// what the adapter called, not on what the output happens to omit.
    /// </summary>
    private sealed class LayoutOnlyRuntimeStub(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        private int inFlight;

        public bool DelayResponse { get; set; }

        public TimeSpan? ResponseDelay { get; set; }

        public int RequestCount { get; private set; }

        public int PeakInFlight { get; private set; }

        public string? LastAuthorizationScheme { get; private set; }

        public string? LastAuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            // Exactly one route, exactly one method: anything else is the adapter reaching for a
            // protocol this contract must never use.
            if (request.Method != HttpMethod.Post
                || request.RequestUri?.AbsoluteUri != Route)
            {
                Assert.Fail(
                    $"The layout adapter called '{request.Method} {request.RequestUri?.AbsoluteUri}', "
                        + "which is not the single layout inference route.");
            }

            var contentType = request.Content?.Headers.ContentType;
            if (contentType?.MediaType != "application/json"
                || contentType.CharSet is not null)
            {
                Assert.Fail(
                    $"The layout request carried Content-Type '{contentType}', not bare application/json.");
            }

            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            var current = Interlocked.Increment(ref inFlight);
            PeakInFlight = Math.Max(PeakInFlight, current);
            try
            {
                if (DelayResponse || ResponseDelay.HasValue)
                {
                    await Task.Delay(ResponseDelay ?? Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return await responseFactory(request);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }
    }
}
