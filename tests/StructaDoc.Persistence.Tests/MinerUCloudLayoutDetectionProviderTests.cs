using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Persistence.Providers;
using StructaDoc.Adapters.Providers;
using StructaDoc.Application.Providers;

namespace StructaDoc.Persistence.Tests;

// The MinerU Cloud layout exchange (ADR-0014). What these tests hold is the issue's acceptance
// surface: the official upload/poll/result protocol and never the custom layout-runtime route,
// credential isolation between the API host and the signed hosts, the observed cloud layout JSON
// reduced to the public layout contract (upright and EXIF-rotated), nested entries not duplicated,
// malformed or incompatible output rejected rather than answered as a false success, the single
// overall deadline, cancellation, and the bounded concurrency. The cloud behind the handler is an
// instrumented stub, so no request leaves the process and nothing here says anything about the
// live service's latency or availability.
public sealed class MinerUCloudLayoutDetectionProviderTests
{
    private const string BatchRoute = CloudApiStub.BatchRoute;

    private const string StatusRoute = CloudApiStub.StatusRoute;

    private const string UploadUrl = CloudApiStub.UploadUrl;

    private const string ResultUrl = CloudApiStub.ResultUrl;

    [Fact]
    public async Task The_exchange_uses_the_official_protocol_and_maps_the_observed_result()
    {
        var cloud = new CloudApiStub();
        var provider = ConfiguredProvider(cloud);
        var image = "cloud-image-bytes"u8.ToArray();

        var result = await provider.DetectLayoutAsync(
            UprightRequest(image),
            TestContext.Current.CancellationToken);

        // The whole exchange in order: batch submission, one waiting-file poll, the signed upload,
        // the completed poll, and the result download. No other route exists on the path.
        Assert.Equal(
            [
                ("POST", BatchRoute),
                ("GET", StatusRoute),
                ("PUT", UploadUrl),
                ("GET", StatusRoute),
                ("GET", ResultUrl),
            ],
            cloud.Requests.Select(request => (request.Method, request.Uri.AbsoluteUri)).ToArray());

        // The public layout answer, reduced from the observed worksheet shape: stored dimensions,
        // the probe's first text region as the first region, the generic labels in block order,
        // and the cloud's own version — never its recognized content.
        Assert.Equal(1240, result.SourceImageWidth);
        Assert.Equal(1754, result.SourceImageHeight);
        Assert.Equal(8, result.Regions.Count);
        var first = result.Regions[0];
        Assert.Equal("title", first.Label);
        Assert.Equal(26.0 / 446.0, first.X0, 5);
        Assert.Equal(71.0 / 631.0, first.Y0, 5);
        Assert.Equal(198.0 / 446.0, first.X1, 5);
        Assert.Equal(129.0 / 631.0, first.Y1, 5);
        Assert.Equal(
            ["title", "text", "image", "text", "table", "formula", "text", "footer"],
            result.Regions.Select(region => region.Label).ToArray());
        Assert.Equal("mineru-cloud", result.Engine);
        Assert.Equal("vlm", result.Model);
        Assert.Equal("3.4.4", result.Version);

        // The one thing the runtime backend never says and this backend always must: the cloud ran
        // its full pipeline, and only the layout of it is here.
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(
            "The MinerU Cloud backend executed its full extraction pipeline; only layout regions are returned.",
            diagnostic);
    }

    [Fact]
    public async Task The_exchange_sends_the_credential_only_to_the_api_host()
    {
        var cloud = new CloudApiStub();
        var provider = ConfiguredProvider(cloud);

        await provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        foreach (var request in cloud.Requests)
        {
            var isApiHost = request.Uri.Host == "mineru.example";
            Assert.Equal(isApiHost ? "Bearer" : null, request.AuthorizationScheme);
            Assert.Equal(isApiHost ? "cloud-token" : null, request.AuthorizationParameter);
        }
    }

    [Fact]
    public async Task The_submission_carries_the_least_work_switches_and_the_configured_model()
    {
        var cloud = new CloudApiStub();
        var provider = ConfiguredProvider(cloud);
        var image = "cloud-image-bytes"u8.ToArray();

        await provider.DetectLayoutAsync(
            UprightRequest(image, mediaType: "image/jpeg"),
            TestContext.Current.CancellationToken);

        var submission = cloud.Requests.Single(request => request.Uri.AbsoluteUri == BatchRoute);
        using var payload = JsonDocument.Parse(submission.Body!);
        var root = payload.RootElement;
        var file = root.GetProperty("files")[0];
        Assert.Equal("page.jpg", file.GetProperty("name").GetString());
        Assert.False(file.GetProperty("is_ocr").GetBoolean());
        Assert.True(file.GetProperty("data_id").GetString()!.Length > 0);
        Assert.Equal("vlm", root.GetProperty("model_version").GetString());
        Assert.False(root.GetProperty("enable_formula").GetBoolean());
        Assert.False(root.GetProperty("enable_table").GetBoolean());
        Assert.Equal("ch", root.GetProperty("language").GetString());

        // The signed upload carried the submitted bytes with a length and no media type, exactly
        // as the signed-transfer rules require.
        var upload = cloud.Requests.Single(request => request.Uri.AbsoluteUri == UploadUrl);
        Assert.Equal(image, upload.Body);
        Assert.Equal(image.LongLength, upload.ContentLength);
        Assert.Null(upload.ContentType);
    }

    [Fact]
    public async Task A_null_model_configuration_submits_the_documented_default()
    {
        var cloud = new CloudApiStub();
        var provider = new MinerUCloudLayoutDetectionProvider(
            new HttpClient(cloud),
            new HttpClient(cloud),
            ReadySource(model: null));

        await provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        var submission = cloud.Requests.Single(request => request.Uri.AbsoluteUri == BatchRoute);
        using var payload = JsonDocument.Parse(submission.Body!);
        Assert.Equal(
            ProviderTypeDescriptors.MinerUCloudDefaultModel,
            payload.RootElement.GetProperty("model_version").GetString());
    }

    [Fact]
    public async Task The_rotated_image_maps_back_to_the_stored_frame_without_double_rotation()
    {
        // The probe's EXIF orientation 6 variant: stored 1754x1240, so the image presents upright
        // as 1240x1754, and the cloud answered with the same upright-frame geometry as the upright
        // submission. The response must be in the stored frame — the one the public contract's
        // width/height describe — so a consumer applying the reported orientation rotates exactly
        // once.
        var cloud = new CloudApiStub();
        var provider = ConfiguredProvider(cloud);

        var result = await provider.DetectLayoutAsync(
            new LayoutDetectionRequest(
                "cloud-image-bytes"u8.ToArray(),
                "image/jpeg",
                storedWidth: 1754,
                storedHeight: 1240,
                exifOrientation: 6),
            TestContext.Current.CancellationToken);

        Assert.Equal(1754, result.SourceImageWidth);
        Assert.Equal(1240, result.SourceImageHeight);
        var first = result.Regions[0];
        Assert.Equal("title", first.Label);
        // Orientation 6 maps an upright box (x0, y0, x1, y1) to the stored frame as
        // (X0, Y0, X1, Y1) = (y0, 1-x1, y1, 1-x0): the upright top edge becomes the stored left
        // edge, and the upright horizontal extent becomes the stored vertical extent.
        Assert.Equal(71.0 / 631.0, first.X0, 5);
        Assert.Equal(129.0 / 631.0, first.X1, 5);
        Assert.Equal(1 - 198.0 / 446.0, first.Y0, 5);
        Assert.Equal(1 - 26.0 / 446.0, first.Y1, 5);
        Assert.Equal(8, result.Regions.Count);
    }

    [Fact]
    public async Task Nested_line_and_span_entries_are_not_reported_as_peer_regions()
    {
        // The fixture's eight main blocks carry sixteen nested lines and seventeen spans between
        // them; any of those surfacing as a region changes the count, so the count is the proof.
        var cloud = new CloudApiStub();
        var provider = ConfiguredProvider(cloud);

        var result = await provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(8, result.Regions.Count);
    }

    [Fact]
    public async Task A_page_the_model_found_nothing_on_is_a_valid_empty_answer()
    {
        var cloud = new CloudApiStub { ResultZip = ZipOf(("layout.json", """
            {"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"_backend":"hybrid","_version_name":"3.4.4"}]}
            """)) };
        var provider = ConfiguredProvider(cloud);

        var result = await provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Regions);
        Assert.Equal(1240, result.SourceImageWidth);
    }

    [Fact]
    public async Task Polling_continues_until_the_extraction_finishes()
    {
        var cloud = new CloudApiStub
        {
            PollStates = ["waiting-file", "pending", "converting", "running", "done"],
        };
        var provider = ConfiguredProvider(cloud);

        var result = await provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(8, result.Regions.Count);
        // Exactly one upload ever: the four later polls never re-sent the file, and the state
        // sequence was observed by five polls in total.
        Assert.Equal(1, cloud.Requests.Count(request => request.Uri.AbsoluteUri == UploadUrl));
        Assert.Equal(5, cloud.Requests.Count(request => request.Uri.AbsoluteUri == StatusRoute));
    }

    [Fact]
    public async Task An_expired_overall_deadline_reports_the_stable_layout_timeout()
    {
        var cloud = new CloudApiStub { PollState = _ => "running" };
        var provider = new MinerUCloudLayoutDetectionProvider(
            new HttpClient(cloud),
            new HttpClient(cloud),
            ReadySource(),
            deadline: TimeSpan.FromMilliseconds(200));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-timeout", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Transient, exception.Category);
        Assert.True(exception.Retryable);
        Assert.Equal(0, cloud.Requests.Count(request => request.Uri.AbsoluteUri == ResultUrl));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_frees_the_execution_slot()
    {
        var cloud = new CloudApiStub { DelayEveryRequest = true };
        var provider = ConfiguredProvider(cloud);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            cancellation.Token));

        // The cancelled execution released its concurrency slot: the next call completes.
        cloud.DelayEveryRequest = false;
        var result = await provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);
        Assert.Equal(8, result.Regions.Count);
    }

    [Fact]
    public async Task Concurrent_exchanges_are_bounded_by_the_fixed_limit()
    {
        var cloud = new CloudApiStub { ResultDelay = TimeSpan.FromMilliseconds(250) };
        var provider = ConfiguredProvider(cloud);

        // One more execution than the bound: the bound, not the arrival count, decides how many
        // are in flight at once. Each exchange downloads its result once, so the completed
        // downloads count the executions whatever poll interleaving occurs between them.
        var executions = Enumerable.Range(0, MinerUCloudLayoutDetectionProvider.MaxConcurrency + 1)
            .Select(_ => provider.DetectLayoutAsync(
                UprightRequest("cloud-image-bytes"u8.ToArray()),
                TestContext.Current.CancellationToken))
            .ToArray();
        var results = await Task.WhenAll(executions);

        Assert.All(results, result => Assert.Equal(8, result.Regions.Count));
        Assert.Equal(
            MinerUCloudLayoutDetectionProvider.MaxConcurrency + 1,
            cloud.Requests.Count(request => request.Uri.AbsoluteUri == ResultUrl));
        Assert.Equal(
            MinerUCloudLayoutDetectionProvider.MaxConcurrency,
            cloud.PeakInFlight);
    }

    [Theory]
    [InlineData(LayoutCloudConfigurationState.NoDefaultProvider, "no Provider configuration is marked default")]
    [InlineData(LayoutCloudConfigurationState.DefaultProviderNotCloud, "not a MinerU Cloud one")]
    [InlineData(LayoutCloudConfigurationState.DefaultProviderDisabled, "disabled")]
    [InlineData(LayoutCloudConfigurationState.CredentialMissing, "no credential")]
    public async Task A_cloud_configuration_that_cannot_serve_answers_the_explicit_unsupported_error(
        LayoutCloudConfigurationState state,
        string expectedPhrase)
    {
        var cloud = new CloudApiStub();
        var provider = new MinerUCloudLayoutDetectionProvider(
            new HttpClient(cloud),
            new HttpClient(cloud),
            new StubCloudSource(new LayoutCloudConfigurationSnapshot(state)));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
            exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Configuration, exception.Category);
        Assert.Contains(expectedPhrase, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, cloud.RequestCount);
    }

    [Fact]
    public async Task A_request_without_the_inspected_dimensions_is_refused()
    {
        var provider = ConfiguredProvider(new CloudApiStub());

        // The two-argument request shape is the runtime backend's; the cloud backend cannot honor
        // the original-image response contract without the inspection facts.
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DetectLayoutAsync(
            new LayoutDetectionRequest("cloud-image-bytes"u8.ToArray(), "image/png"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_provider_rejection_carries_the_bounded_reason()
    {
        var cloud = new CloudApiStub
        {
            BatchResponse = """
                {"code": 4010, "msg": "Invalid token", "trace_id": "trace-2049"}
                """,
        };
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-cloud-submit-rejected", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
        // The code, the message, and the trace identifier — and nothing else of the body.
        Assert.Contains("code=4010", exception.Message, StringComparison.Ordinal);
        Assert.Contains("msg=Invalid token", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trace_id=trace-2049", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length <= 2048, exception.Message);
    }

    [Fact]
    public async Task A_failed_extraction_is_a_permanent_failure()
    {
        var cloud = new CloudApiStub { PollStates = ["failed"] };
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-cloud-task-failed", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
    }

    [Fact]
    public async Task An_unknown_extraction_state_is_a_transient_failure()
    {
        var cloud = new CloudApiStub { PollStates = ["warping"] };
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-cloud-task-state-unknown", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Transient, exception.Category);
    }

    [Theory]
    [InlineData("batch", HttpStatusCode.InternalServerError, "mineru-layout-cloud-submit-http-500", ProviderFailureCategory.Transient)]
    [InlineData("status", HttpStatusCode.Unauthorized, "mineru-layout-cloud-status-http-401", ProviderFailureCategory.Configuration)]
    [InlineData("upload", HttpStatusCode.Forbidden, "mineru-layout-cloud-upload-http-403", ProviderFailureCategory.Permanent)]
    [InlineData("result", HttpStatusCode.NotFound, "mineru-layout-cloud-result-http-404", ProviderFailureCategory.Permanent)]
    public async Task Http_failures_on_every_leg_map_to_the_provider_categories(
        string leg,
        HttpStatusCode status,
        string expectedCode,
        ProviderFailureCategory expectedCategory)
    {
        var cloud = new CloudApiStub();
        cloud.FailLeg[leg] = status;
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(expectedCategory, exception.Category);
    }

    [Theory]
    [InlineData("no-layout-entry")]
    [InlineData("ambiguous-layout-entries")]
    [InlineData("layout-not-json")]
    [InlineData("missing-pdf-info")]
    [InlineData("two-pages")]
    [InlineData("missing-page-size")]
    [InlineData("aspect-mismatch")]
    [InlineData("bbox-outside-page")]
    [InlineData("bbox-inverted")]
    [InlineData("label-with-spaces")]
    public async Task Incompatible_or_malformed_output_is_rejected_rather_than_answered_as_success(string shape)
    {
        var cloud = new CloudApiStub { ResultZip = ShapedResultZip(shape) };
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-cloud-layout-response-invalid", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
        Assert.True(exception.Message.Length <= 2048, exception.Message);
    }

    [Fact]
    public async Task The_para_blocks_spelling_maps_the_same_as_preproc_blocks()
    {
        var cloud = new CloudApiStub { ResultZip = ShapedResultZip("para-blocks-spelling") };
        var provider = ConfiguredProvider(cloud);

        var result = await provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        var region = Assert.Single(result.Regions);
        Assert.Equal("table", region.Label);
    }

    [Fact]
    public async Task An_oversized_result_archive_is_refused_mid_download()
    {
        var cloud = new CloudApiStub
        {
            ResultStreamLength = MinerUCloudLayoutDetectionProvider.MaximumArchiveBytes + 1,
        };
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.DetectLayoutAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-layout-cloud-archive-too-large", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
    }

    [Fact]
    public void The_orientation_inverse_mapping_is_the_exif_transform_reversed_for_every_value()
    {
        // Hand-checked corner mapping of the box (0.2, 0.1, 0.6, 0.4) for all eight orientations:
        // the values a consumer applying the reported orientation arrives back at.
        var actual = Enumerable.Range(1, 8)
            .Select(orientation => MinerUCloudLayoutDetectionProvider.ToStoredFrame(0.2, 0.1, 0.6, 0.4, orientation))
            .ToArray();
        double[][] expected =
        [
            [0.2, 0.1, 0.6, 0.4],
            [0.4, 0.1, 0.8, 0.4],
            [0.4, 0.6, 0.8, 0.9],
            [0.2, 0.6, 0.6, 0.9],
            [0.1, 0.2, 0.4, 0.6],
            [0.1, 0.4, 0.4, 0.8],
            [0.6, 0.4, 0.9, 0.8],
            [0.6, 0.2, 0.9, 0.6],
        ];

        for (var orientation = 0; orientation < 8; orientation++)
        {
            Assert.Equal(expected[orientation][0], actual[orientation].X0, 10);
            Assert.Equal(expected[orientation][1], actual[orientation].Y0, 10);
            Assert.Equal(expected[orientation][2], actual[orientation].X1, 10);
            Assert.Equal(expected[orientation][3], actual[orientation].Y1, 10);
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MinerUCloudLayoutDetectionProvider.ToStoredFrame(0, 0, 1, 1, 9));
    }

    [Fact]
    public void Registration_with_the_cloud_backend_registers_only_the_cloud_implementation()
    {
        // Descriptor-level assertions: activating the cloud adapter would need the business
        // database context the source reads, which a bare container does not have — the composed
        // host covers activation, and these hold the composition itself.
        var services = new ServiceCollection()
            .AddStructaDocParseProviders()
            .AddStructaDocLayoutDetection(
                new LayoutRuntimeOptions { BaseUrl = "https://runtime.example" },
                new LayoutOptions { Backend = LayoutBackendNames.MinerUCloud });

        var provider = Assert.Single(
            services,
            service => service.ServiceType == typeof(ILayoutDetectionProvider));
        // A factory registration: activating it needs the scoped configuration source, so the
        // composed host covers activation while this holds what the factory builds.
        Assert.Null(provider.ImplementationType);
        Assert.NotNull(provider.ImplementationFactory);
        Assert.Equal(ServiceLifetime.Scoped, provider.Lifetime);

        var source = Assert.Single(
            services,
            service => service.ServiceType == typeof(ILayoutCloudConfigurationSource));
        Assert.Equal(typeof(EfCoreLayoutCloudConfigurationSource), source.ImplementationType);

        // The runtime section being set does not register a second implementation when the cloud
        // backend is the selection — the Single assertion above holds it — and the published
        // selection is the cloud one.
        using var built = services.BuildServiceProvider();
        Assert.Equal(
            LayoutBackend.MinerUCloud,
            built.GetRequiredService<LayoutBackendSelection>().Backend);

        // The default backend keeps registering the runtime adapter only; its registration has no
        // database dependency, so the built provider hands back the real instance.
        var runtimeServices = new ServiceCollection()
            .AddStructaDocLayoutDetection(
                new LayoutRuntimeOptions { BaseUrl = "https://runtime.example" },
                new LayoutOptions());
        using var runtimeBuilt = runtimeServices.BuildServiceProvider();
        Assert.IsType<MinerULayoutDetectionProvider>(
            runtimeBuilt.GetRequiredService<ILayoutDetectionProvider>());
        Assert.Equal(
            LayoutBackend.LayoutRuntime,
            runtimeBuilt.GetRequiredService<LayoutBackendSelection>().Backend);
    }

    private static LayoutDetectionRequest UprightRequest(byte[] image, string mediaType = "image/png") => new(
        image,
        mediaType,
        storedWidth: 1240,
        storedHeight: 1754,
        exifOrientation: null);

    private static ProviderExecutionConfiguration ReadyConfiguration(string? model = "vlm") => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ProviderTypes.MinerUCloud,
        new Uri("https://mineru.example/"),
        model,
        backend: null,
        new ProviderCredential("cloud-token"));

    private static StubCloudSource ReadySource(string? model = "vlm") => new(
        new LayoutCloudConfigurationSnapshot(
            LayoutCloudConfigurationState.Ready,
            ReadyConfiguration(model)));

    private static MinerUCloudLayoutDetectionProvider ConfiguredProvider(CloudApiStub cloud) => new(
        new HttpClient(cloud),
        new HttpClient(cloud),
        ReadySource());

    private static byte[] FixtureLayout() => CloudStubArchives.Fixture("cloud-layout-worksheet.json");

    private static byte[] ZipOf(params (string Name, string Content)[] entries) =>
        CloudStubArchives.ZipOf(entries);

    private static byte[] ZipOfBytes(params (string Name, byte[] Content)[] entries) =>
        CloudStubArchives.ZipOfBytes(entries);

    /// <summary>
    /// The result archive each malformed-output case needs, built from the fixture's real shape so
    /// a rejection is about the one field the case breaks, not about an invented format.
    /// </summary>
    private static byte[] ShapedResultZip(string shape)
    {
        var layout = Encoding.UTF8.GetString(FixtureLayout());
        return shape switch
        {
            "no-layout-entry" => ZipOf(("full.md", "# Synthetic"), ("doc_model.json", "{}")),
            "ambiguous-layout-entries" => ZipOf(
                ("first/layout.json", layout),
                ("second/layout.json", layout)),
            "layout-not-json" => ZipOfBytes(("layout.json", "not json at all"u8.ToArray())),
            "missing-pdf-info" => ZipOf(("layout.json", """{"pages":[]}""")),
            "two-pages" => ZipOf(
                ("layout.json",
                """{"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"preproc_blocks":[]},{"page_idx":1,"page_size":[446.0,631.0],"preproc_blocks":[]}]}""")),
            "missing-page-size" => ZipOf(
                ("layout.json", """{"pdf_info":[{"page_idx":0,"preproc_blocks":[]}]}""")),
            "aspect-mismatch" => ZipOf(
                ("layout.json",
                """{"pdf_info":[{"page_idx":0,"page_size":[631.0,446.0],"preproc_blocks":[]}]}""")),
            "bbox-outside-page" => ZipOf(
                ("layout.json",
                """{"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"preproc_blocks":[{"type":"text","bbox":[0,0,500,10]}]}]}""")),
            "bbox-inverted" => ZipOf(
                ("layout.json",
                """{"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"preproc_blocks":[{"type":"text","bbox":[100,10,50,60]}]}]}""")),
            "label-with-spaces" => ZipOf(
                ("layout.json",
                """{"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"preproc_blocks":[{"type":"text block","bbox":[26,71,198,129]}]}]}""")),
            "para-blocks-spelling" => ZipOf(
                ("layout.json",
                """{"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"para_blocks":[{"type":"table","bbox":[40,295,420,400],"lines":[]}]}]}""")),
            _ => throw new NotSupportedException($"The result shape '{shape}' has no builder."),
        };
    }
}
