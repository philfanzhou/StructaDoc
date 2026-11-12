using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Persistence.Providers;
using StructaDoc.Adapters.Providers;
using StructaDoc.Application.Providers;

namespace StructaDoc.Persistence.Tests;

// The MinerU Cloud image text-recognition exchange (ADR-0015). It reuses the layout mode's cloud
// exchange, so these tests hold what is new: the recognition submission switches, the observed
// layout JSON shapes reduced to blocks with text, lines, table HTML, and formula LaTeX (flat and
// nested, upright and EXIF-rotated), nested entries reduced into their block rather than reported
// as peers, malformed output rejected under the recognition code family, the shared cloud exchange
// bound, and the off-by-default registration. The cloud is the instrumented stub; nothing leaves
// the process.
public sealed class MinerUCloudImageRecognitionProviderTests
{
    [Fact]
    public async Task The_exchange_uses_the_official_protocol_with_the_recognition_switches()
    {
        var cloud = new CloudApiStub();
        var provider = ConfiguredProvider(cloud);
        var image = "cloud-image-bytes"u8.ToArray();

        await provider.RecognizeAsync(UprightRequest(image, "image/jpeg"), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ("POST", CloudApiStub.BatchRoute),
                ("GET", CloudApiStub.StatusRoute),
                ("PUT", CloudApiStub.UploadUrl),
                ("GET", CloudApiStub.StatusRoute),
                ("GET", CloudApiStub.ResultUrl),
            ],
            cloud.Requests.Select(request => (request.Method, request.Uri.AbsoluteUri)).ToArray());

        // Recognition asks for the full work its answer needs, unlike the layout mode's
        // least-work switches.
        var submission = cloud.Requests.Single(request => request.Uri.AbsoluteUri == CloudApiStub.BatchRoute);
        using var payload = JsonDocument.Parse(submission.Body!);
        var root = payload.RootElement;
        var file = root.GetProperty("files")[0];
        Assert.Equal("page.jpg", file.GetProperty("name").GetString());
        Assert.True(file.GetProperty("is_ocr").GetBoolean());
        Assert.True(root.GetProperty("enable_formula").GetBoolean());
        Assert.True(root.GetProperty("enable_table").GetBoolean());
        Assert.Equal("vlm", root.GetProperty("model_version").GetString());

        // The credential goes to the API host only; the signed hosts never see it, and the upload
        // carries the submitted bytes with a length and no media type.
        foreach (var request in cloud.Requests)
        {
            var isApiHost = request.Uri.Host == "mineru.example";
            Assert.Equal(isApiHost ? "cloud-token" : null, request.AuthorizationParameter);
        }

        var upload = cloud.Requests.Single(request => request.Uri.AbsoluteUri == CloudApiStub.UploadUrl);
        Assert.Equal(image, upload.Body);
        Assert.Null(upload.ContentType);
    }

    [Fact]
    public async Task The_observed_flat_shape_reduces_to_blocks_with_text_html_and_latex()
    {
        var provider = ConfiguredProvider(new CloudApiStub());

        var result = await provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(1240, result.SourceImageWidth);
        Assert.Equal(1754, result.SourceImageHeight);
        Assert.Equal("mineru-cloud", result.Engine);
        Assert.Equal("vlm", result.Model);
        Assert.Equal("3.4.4", result.Version);
        Assert.Empty(result.Diagnostics);

        // Eight main blocks in, eight blocks out: the fixture's sixteen nested lines and
        // seventeen spans are reduced into their blocks, never reported as peers.
        Assert.Equal(
            ["title", "text", "image", "text", "table", "formula", "text", "footer"],
            result.Blocks.Select(block => block.Type).ToArray());

        var title = result.Blocks[0];
        Assert.Equal("Synthetic worksheet heading", title.Content);
        Assert.Equal("plain", title.ContentFormat);
        Assert.Equal(26.0 / 446.0, title.Box.X0, 5);
        Assert.Equal(71.0 / 631.0, title.Box.Y0, 5);
        Assert.Equal(198.0 / 446.0, title.Box.X1, 5);
        Assert.Equal(129.0 / 631.0, title.Box.Y1, 5);

        // A text block keeps each printed line with its own box, and its content is the lines.
        var instructions = result.Blocks[1];
        Assert.Equal(
            ["Synthetic instruction line one.", "Synthetic instruction line two."],
            instructions.Lines.Select(line => line.Text).ToArray());
        Assert.Equal("Synthetic instruction line one.\nSynthetic instruction line two.", instructions.Content);
        Assert.Equal(179.0 / 631.0, instructions.Lines[1].Box.Y0, 5);
        Assert.Equal(205.0 / 631.0, instructions.Lines[1].Box.Y1, 5);

        // Figures carry geometry only.
        var figure = result.Blocks[2];
        Assert.Null(figure.Content);
        Assert.Null(figure.ContentFormat);
        Assert.Empty(figure.Lines);

        var table = result.Blocks[4];
        Assert.Equal("<table><tr><td>Synthetic</td><td>Cells</td></tr></table>", table.Content);
        Assert.Equal("html", table.ContentFormat);
        Assert.Empty(table.Lines);

        var formula = result.Blocks[5];
        Assert.Equal("E = mc^{2}", formula.Content);
        Assert.Equal("latex", formula.ContentFormat);
        Assert.Empty(formula.Lines);

        Assert.Equal("Synthetic page footer", result.Blocks[7].Content);
    }

    [Fact]
    public async Task The_nested_word_list_shape_keeps_rows_captions_and_reading_order()
    {
        var cloud = new CloudApiStub { ResultZip = WordListZip() };
        var provider = ConfiguredProvider(cloud);

        var result = await provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        // `para_blocks` wins over the `preproc_blocks` spelling, and the discarded header is not
        // read: the first block is the title, and the discarded text appears nowhere.
        Assert.Equal(
            ["title", "table", "text", "list", "formula", "text", "image", "unknown"],
            result.Blocks.Select(block => block.Type).ToArray());
        Assert.Equal("Unit 3 Word List", result.Blocks[0].Content);
        Assert.DoesNotContain(result.Blocks, block => block.Content?.Contains("Preproc", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Blocks, block => block.Content?.Contains("Discarded", StringComparison.Ordinal) == true);

        // A table's nested body gives its HTML; its caption and footnote children feed those
        // slots instead of becoming peer blocks or lines.
        var table = result.Blocks[1];
        Assert.Equal("html", table.ContentFormat);
        Assert.StartsWith("<table><tr><td>apple</td>", table.Content, StringComparison.Ordinal);
        Assert.Equal("Table 1 Core words", table.Caption);
        Assert.Equal("Synthetic table note", table.Footnote);
        Assert.Empty(table.Lines);

        // One printed row is one line: the word, its part of speech, and its definition stay
        // together, joined the way the backend renders mixed Latin and CJK spans.
        var rows = result.Blocks[2];
        Assert.Equal(
            ["careful adj. 小心的", "decide v. 决定"],
            rows.Lines.Select(line => line.Text).ToArray());
        Assert.Equal("careful adj. 小心的\ndecide v. 决定", rows.Content);

        // A list's nested items are its lines.
        var list = result.Blocks[3];
        Assert.Equal(
            ["1. eager adj. 渴望的", "2. fierce adj. 凶猛的"],
            list.Lines.Select(line => line.Text).ToArray());

        // The interline equation's LaTeX in its `content` spelling, and an inline equation kept
        // in its line with Markdown math delimiters.
        Assert.Equal("a^2 + b^2 = c^2", result.Blocks[4].Content);
        Assert.Equal("latex", result.Blocks[4].ContentFormat);
        Assert.Equal("Area is $\\pi r^2$.", result.Blocks[5].Content);

        // A figure keeps its caption but no content of its own.
        var figure = result.Blocks[6];
        Assert.Null(figure.Content);
        Assert.Equal("Figure 1 A synthetic apple", figure.Caption);

        // A type outside the registered set is `unknown`, and keeps its text.
        Assert.Equal("Synthetic margin note", result.Blocks[7].Content);
    }

    [Fact]
    public async Task The_rotated_image_maps_block_and_line_boxes_back_to_the_stored_frame()
    {
        // Stored 1754x1240 with EXIF orientation 6: the cloud answered in the upright frame, and
        // every box — blocks and lines alike — comes back in the stored frame, so a consumer
        // applying the reported orientation rotates exactly once.
        var provider = ConfiguredProvider(new CloudApiStub());

        var result = await provider.RecognizeAsync(
            new LayoutDetectionRequest(
                "cloud-image-bytes"u8.ToArray(),
                "image/jpeg",
                storedWidth: 1754,
                storedHeight: 1240,
                exifOrientation: 6),
            TestContext.Current.CancellationToken);

        Assert.Equal(1754, result.SourceImageWidth);
        Assert.Equal(1240, result.SourceImageHeight);
        var title = result.Blocks[0].Box;
        Assert.Equal(71.0 / 631.0, title.X0, 5);
        Assert.Equal(129.0 / 631.0, title.X1, 5);
        Assert.Equal(1 - 198.0 / 446.0, title.Y0, 5);
        Assert.Equal(1 - 26.0 / 446.0, title.Y1, 5);

        // Upright line two of the instructions is [40, 179, 420, 205] over 446x631.
        var line = result.Blocks[1].Lines[1].Box;
        Assert.Equal(179.0 / 631.0, line.X0, 5);
        Assert.Equal(205.0 / 631.0, line.X1, 5);
        Assert.Equal(1 - 420.0 / 446.0, line.Y0, 5);
        Assert.Equal(1 - 40.0 / 446.0, line.Y1, 5);
    }

    [Fact]
    public async Task A_page_the_backend_found_nothing_on_is_a_valid_empty_answer()
    {
        var cloud = new CloudApiStub
        {
            ResultZip = CloudStubArchives.ZipOf(("layout.json", """
                {"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"para_blocks":[],"_version_name":"3.4.4"}]}
                """)),
        };
        var provider = ConfiguredProvider(cloud);

        var result = await provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Blocks);
        Assert.Equal(1240, result.SourceImageWidth);
    }

    [Theory]
    [InlineData("no-layout-entry")]
    [InlineData("two-pages")]
    [InlineData("aspect-mismatch")]
    [InlineData("block-without-type")]
    [InlineData("block-without-bbox")]
    [InlineData("lines-not-array")]
    [InlineData("spans-not-array")]
    [InlineData("span-content-not-text")]
    [InlineData("line-bbox-outside-page")]
    [InlineData("text-line-without-bbox")]
    [InlineData("nested-blocks-not-array")]
    [InlineData("nested-too-deep")]
    public async Task Malformed_or_incompatible_output_is_rejected_rather_than_answered_as_success(string shape)
    {
        var cloud = new CloudApiStub { ResultZip = ShapedResultZip(shape) };
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-recognition-cloud-layout-response-invalid", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
        Assert.True(exception.Message.Length <= 2048, exception.Message);
        // A rejection names the broken field, never the recognized text around it.
        Assert.DoesNotContain("Synthetic", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LayoutCloudConfigurationState.NoDefaultProvider, "send recognition requests to")]
    [InlineData(LayoutCloudConfigurationState.DefaultProviderNotCloud, "not a MinerU Cloud one")]
    [InlineData(LayoutCloudConfigurationState.DefaultProviderDisabled, "disabled")]
    [InlineData(LayoutCloudConfigurationState.CredentialMissing, "no credential")]
    public async Task A_cloud_configuration_that_cannot_serve_answers_the_recognition_unsupported_error(
        LayoutCloudConfigurationState state,
        string expectedPhrase)
    {
        var cloud = new CloudApiStub();
        var provider = new MinerUCloudImageRecognitionProvider(
            new HttpClient(cloud),
            new HttpClient(cloud),
            new StubCloudSource(new LayoutCloudConfigurationSnapshot(state)));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal(IImageRecognitionProviderResolver.UnsupportedCapabilityErrorCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Configuration, exception.Category);
        Assert.Contains(expectedPhrase, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, cloud.RequestCount);
    }

    [Fact]
    public async Task An_expired_overall_deadline_reports_the_stable_recognition_timeout()
    {
        var cloud = new CloudApiStub { PollState = _ => "running" };
        var provider = new MinerUCloudImageRecognitionProvider(
            new HttpClient(cloud),
            new HttpClient(cloud),
            ReadySource(),
            deadline: TimeSpan.FromMilliseconds(200));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-recognition-timeout", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Transient, exception.Category);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_frees_the_shared_slot()
    {
        var cloud = new CloudApiStub { DelayEveryRequest = true };
        var limiter = new MinerUCloudExchangeLimiter();
        var provider = ConfiguredProvider(cloud, limiter);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            cancellation.Token));

        cloud.DelayEveryRequest = false;
        var result = await provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken);
        Assert.Equal(8, result.Blocks.Count);
    }

    [Theory]
    [InlineData("batch", HttpStatusCode.InternalServerError, "mineru-recognition-cloud-submit-http-500", ProviderFailureCategory.Transient)]
    [InlineData("status", HttpStatusCode.Unauthorized, "mineru-recognition-cloud-status-http-401", ProviderFailureCategory.Configuration)]
    [InlineData("upload", HttpStatusCode.Forbidden, "mineru-recognition-cloud-upload-http-403", ProviderFailureCategory.Permanent)]
    [InlineData("result", HttpStatusCode.NotFound, "mineru-recognition-cloud-result-http-404", ProviderFailureCategory.Permanent)]
    public async Task Http_failures_carry_the_recognition_code_family(
        string leg,
        HttpStatusCode status,
        string expectedCode,
        ProviderFailureCategory expectedCategory)
    {
        var cloud = new CloudApiStub();
        cloud.FailLeg[leg] = status;
        var provider = ConfiguredProvider(cloud);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.RecognizeAsync(
            UprightRequest("cloud-image-bytes"u8.ToArray()),
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(expectedCategory, exception.Category);
    }

    [Fact]
    public async Task Layout_and_recognition_exchanges_share_one_concurrency_bound()
    {
        // The Host registers one limiter for both modes, because they spend the same cloud
        // account: three layout and three recognition exchanges together still run at most the
        // fixed number at once.
        var cloud = new CloudApiStub { ResultDelay = TimeSpan.FromMilliseconds(250) };
        var limiter = new MinerUCloudExchangeLimiter();
        var layout = new MinerUCloudLayoutDetectionProvider(
            new HttpClient(cloud),
            new HttpClient(cloud),
            ReadySource(),
            limiter: limiter);
        var recognition = ConfiguredProvider(cloud, limiter);

        var executions = Enumerable.Range(0, 3)
            .SelectMany(_ => new Task[]
            {
                layout.DetectLayoutAsync(
                    UprightRequest("cloud-image-bytes"u8.ToArray()),
                    TestContext.Current.CancellationToken),
                recognition.RecognizeAsync(
                    UprightRequest("cloud-image-bytes"u8.ToArray()),
                    TestContext.Current.CancellationToken),
            })
            .ToArray();
        await Task.WhenAll(executions);

        Assert.Equal(6, cloud.Requests.Count(request => request.Uri.AbsoluteUri == CloudApiStub.ResultUrl));
        Assert.Equal(MinerUCloudExchangeLimiter.MaxConcurrency, cloud.PeakInFlight);
    }

    [Fact]
    public void Registration_is_off_by_default_and_registers_only_the_cloud_adapter_when_on()
    {
        // Off: no implementation, so the resolution answers the explicit unsupported error.
        var off = new ServiceCollection()
            .AddStructaDocImageRecognition(new RecognitionOptions());
        Assert.DoesNotContain(off, service => service.ServiceType == typeof(IImageRecognitionProvider));
        using (var built = off.BuildServiceProvider())
        {
            var exception = Assert.Throws<ProviderException>(
                () => built.GetRequiredService<IImageRecognitionProviderResolver>().Resolve());
            Assert.Equal(IImageRecognitionProviderResolver.UnsupportedCapabilityErrorCode, exception.ErrorCode);
            Assert.Equal(ProviderFailureCategory.Configuration, exception.Category);
        }

        // On, beside the cloud layout backend: one recognition adapter, and the cloud exchange
        // services registered once for both modes.
        var on = new ServiceCollection()
            .AddStructaDocParseProviders()
            .AddStructaDocLayoutDetection(
                new LayoutRuntimeOptions(),
                new LayoutOptions { Backend = LayoutBackendNames.MinerUCloud })
            .AddStructaDocImageRecognition(new RecognitionOptions { Enabled = true });
        var provider = Assert.Single(on, service => service.ServiceType == typeof(IImageRecognitionProvider));
        Assert.Equal(ServiceLifetime.Scoped, provider.Lifetime);
        var source = Assert.Single(on, service => service.ServiceType == typeof(ILayoutCloudConfigurationSource));
        Assert.Equal(typeof(EfCoreLayoutCloudConfigurationSource), source.ImplementationType);
        var limiter = Assert.Single(on, service => service.ServiceType == typeof(MinerUCloudExchangeLimiter));
        Assert.Equal(ServiceLifetime.Singleton, limiter.Lifetime);

        // Turning recognition on does not change the layout selection or add a layout adapter.
        Assert.Single(on, service => service.ServiceType == typeof(ILayoutDetectionProvider));
    }

    [Fact]
    public void Recognition_on_with_the_runtime_layout_backend_still_registers_the_cloud_services()
    {
        // The two settings are independent: recognition uses the cloud whatever Layout:Backend is.
        var services = new ServiceCollection()
            .AddStructaDocParseProviders()
            .AddStructaDocLayoutDetection(new LayoutRuntimeOptions(), new LayoutOptions())
            .AddStructaDocImageRecognition(new RecognitionOptions { Enabled = true });

        Assert.Single(services, service => service.ServiceType == typeof(IImageRecognitionProvider));
        Assert.Single(services, service => service.ServiceType == typeof(ILayoutCloudConfigurationSource));
        using var built = services.BuildServiceProvider();
        Assert.Equal(LayoutBackend.LayoutRuntime, built.GetRequiredService<LayoutBackendSelection>().Backend);
    }

    private static LayoutDetectionRequest UprightRequest(byte[] image, string mediaType = "image/png") => new(
        image,
        mediaType,
        storedWidth: 1240,
        storedHeight: 1754,
        exifOrientation: null);

    private static StubCloudSource ReadySource() => new(
        new LayoutCloudConfigurationSnapshot(
            LayoutCloudConfigurationState.Ready,
            new ProviderExecutionConfiguration(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ProviderTypes.MinerUCloud,
                new Uri("https://mineru.example/"),
                "vlm",
                backend: null,
                new ProviderCredential("cloud-token"))));

    private static MinerUCloudImageRecognitionProvider ConfiguredProvider(
        CloudApiStub cloud,
        MinerUCloudExchangeLimiter? limiter = null) => new(
        new HttpClient(cloud),
        new HttpClient(cloud),
        ReadySource(),
        limiter: limiter);

    private static byte[] WordListZip() => CloudStubArchives.ZipOfBytes(
        ("layout.json", CloudStubArchives.Fixture("cloud-recognition-wordlist.json")));

    /// <summary>
    /// One broken field per case, on otherwise valid page structure, so a rejection is about that
    /// field and nothing else.
    /// </summary>
    private static byte[] ShapedResultZip(string shape)
    {
        static byte[] Page(string blocks) => CloudStubArchives.ZipOf(
            ("layout.json", "{\"pdf_info\":[{\"page_idx\":0,\"page_size\":[446.0,631.0],\"para_blocks\":" + blocks + "}]}"));

        const string Span = "{\"type\":\"text\",\"bbox\":[40,150,420,176],\"content\":\"Synthetic text\"}";
        return shape switch
        {
            "no-layout-entry" => CloudStubArchives.ZipOf(("full.md", "# Synthetic")),
            "two-pages" => CloudStubArchives.ZipOf(
                ("layout.json",
                """{"pdf_info":[{"page_idx":0,"page_size":[446.0,631.0],"para_blocks":[]},{"page_idx":1,"page_size":[446.0,631.0],"para_blocks":[]}]}""")),
            "aspect-mismatch" => CloudStubArchives.ZipOf(
                ("layout.json", """{"pdf_info":[{"page_idx":0,"page_size":[631.0,446.0],"para_blocks":[]}]}""")),
            "block-without-type" => Page("[{\"bbox\":[40,150,420,205],\"lines\":[]}]"),
            "block-without-bbox" => Page("[{\"type\":\"text\",\"lines\":[]}]"),
            "lines-not-array" => Page("[{\"type\":\"text\",\"bbox\":[40,150,420,205],\"lines\":{}}]"),
            "spans-not-array" => Page(
                "[{\"type\":\"text\",\"bbox\":[40,150,420,205],\"lines\":[{\"bbox\":[40,150,420,176],\"spans\":\"Synthetic\"}]}]"),
            "span-content-not-text" => Page(
                "[{\"type\":\"text\",\"bbox\":[40,150,420,205],\"lines\":[{\"bbox\":[40,150,420,176],\"spans\":[{\"type\":\"text\",\"content\":42}]}]}]"),
            "line-bbox-outside-page" => Page(
                "[{\"type\":\"text\",\"bbox\":[40,150,420,205],\"lines\":[{\"bbox\":[40,150,500,176],\"spans\":[" + Span + "]}]}]"),
            "text-line-without-bbox" => Page(
                "[{\"type\":\"text\",\"bbox\":[40,150,420,205],\"lines\":[{\"spans\":[" + Span + "]}]}]"),
            "nested-blocks-not-array" => Page("[{\"type\":\"table\",\"bbox\":[40,150,420,205],\"blocks\":{}}]"),
            "nested-too-deep" => Page(
                "[{\"type\":\"list\",\"bbox\":[40,150,420,205],\"blocks\":[" + Nest(5) + "]}]"),
            _ => throw new NotSupportedException($"The result shape '{shape}' has no builder."),
        };
    }

    private static string Nest(int depth)
    {
        var builder = new StringBuilder();
        for (var level = 0; level < depth; level++)
        {
            builder.Append("{\"type\":\"text\",\"bbox\":[40,150,420,205],\"blocks\":[");
        }

        builder.Append("{\"type\":\"text\",\"bbox\":[40,150,420,205]}");
        for (var level = 0; level < depth; level++)
        {
            builder.Append("]}");
        }

        return builder.ToString();
    }
}
