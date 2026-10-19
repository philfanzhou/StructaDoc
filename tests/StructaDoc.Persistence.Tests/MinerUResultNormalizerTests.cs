using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

namespace StructaDoc.Persistence.Tests;

public sealed class MinerUResultNormalizerTests
{
    // Fields the fixture carries that the normalizer deliberately does not read. anchor has no
    // canonical field; it stays available in the retained content-list Artifact.
    private static readonly IReadOnlySet<string> IgnoredContentListFields = new HashSet<string>(
        [
            "anchor",
        ],
        StringComparer.Ordinal);

    private const string TableHtml =
        "<table><tr><td>Name</td><td>Value</td></tr><tr><td>Alpha</td><td>1</td></tr></table>";

    [Fact]
    public async Task Normalizes_the_mineru_pipeline_fixture_idempotently()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var contentList = ReadPipelineFixture();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            [
                ("full.md", "# Chapter 1 Getting Started"u8.ToArray()),
                ("content_list.json", contentList),
                ("content_list_v2.json", "[]"u8.ToArray()),
                ("layout.json", "{}"u8.ToArray()),
                ("model.json", "{}"u8.ToArray()),
                .. PipelineFixtureImagePaths(contentList).Select(path => (path, JpegBytes())),
            ]);
        var request = new ProviderResultNormalizationRequest(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive,
            "pipeline");

        var first = await environment.Normalizer.NormalizeAsync(
            request,
            TestContext.Current.CancellationToken);
        var replay = await environment.Normalizer.NormalizeAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.True(environment.Normalizer.Supports(ProviderTypes.MinerUCloud));
        Assert.True(environment.Normalizer.Supports(ProviderTypes.MinerULocal));
        Assert.Equal(ParseBundleValidator.ComputeFingerprint(first), ParseBundleValidator.ComputeFingerprint(replay));
        Assert.Equal(first.Blocks.Select(block => block.Id), replay.Blocks.Select(block => block.Id));
        Assert.Equal(first.Assets.Select(asset => asset.Id), replay.Assets.Select(asset => asset.Id));
        Assert.True(ParseBundleValidator.Validate(first).IsValid);
        Assert.Equal(4, first.Assets.Count);
        Assert.All(first.Assets, asset => Assert.Equal("image/jpeg", asset.MediaType));
        Assert.Equal(6, first.Artifacts.Count);
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.ProviderArchive);
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.Markdown);
        Assert.Equal(2, first.Artifacts.Count(artifact => artifact.Type == ArtifactTypes.ContentList));
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.Layout);
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.ModelOutput);

        // One Page per distinct page_idx, and every fixture item carries a page.
        Assert.Equal([1, 2, 3], first.Pages.Select(page => page.Number));
        Assert.Equal(
            ["{\"providerPageId\":0}", "{\"providerPageId\":1}", "{\"providerPageId\":2}"],
            first.Pages.Select(page => page.SourceLocatorJson));
        Assert.All(first.Blocks, block => Assert.NotNull(block.PageNumber));
        Assert.Equal(Enumerable.Range(0, first.Blocks.Count), first.Blocks.Select(block => block.Sequence));

        var assetIds = first.Assets.Select(asset => asset.Id).ToHashSet();
        Assert.Collection(
            first.Blocks,
            block => AssertBlock(block, 1, "header", null, "Synthetic Field Guide", "plain"),
            block =>
            {
                AssertBlock(block, 1, "title", "heading-1", "Chapter 1 Getting Started", "plain");
                Assert.Equal(0.118, block.BoundingBox!.X0, 10);
                Assert.Equal(0.131, block.BoundingBox.Y1, 10);
                Assert.Null(block.Confidence);
            },
            block => AssertBlock(
                block,
                1,
                "text",
                null,
                "This paragraph is synthetic sample text for the MinerU fixture.",
                "plain"),
            block =>
            {
                AssertBlock(block, 1, "formula", null, "$$\nE = m c ^ {2}\n$$", "latex");
                Assert.Contains(block.AssetId!.Value, assetIds);
            },
            block => AssertBlock(
                block,
                1,
                "list",
                "text",
                "First synthetic item\nSecond synthetic item",
                "plain"),
            block => AssertBlock(block, 1, "footnote", null, "1 A synthetic page footnote.", "plain"),
            block => AssertBlock(block, 1, "page-number", null, "1", "plain"),
            block =>
            {
                AssertBlock(block, 2, "image", null, null, null);
                Assert.Contains(block.AssetId!.Value, assetIds);
            },
            block =>
            {
                AssertBlock(block, 2, "table", null, TableHtml, "html");
                Assert.Contains(block.AssetId!.Value, assetIds);
            },
            block =>
            {
                AssertBlock(
                    block,
                    2,
                    "image",
                    "chart",
                    "A synthetic bar chart rising from left to right.",
                    "plain");
                Assert.Contains(block.AssetId!.Value, assetIds);
            },
            block => AssertBlock(block, 2, "unknown", "aside-text", "Synthetic margin note", "plain"),
            block => AssertBlock(block, 2, "footer", null, "Synthetic Field Guide footer", "plain"),
            block => AssertBlock(block, 2, "page-number", null, "2", "plain"),
            block => AssertBlock(
                block,
                3,
                "code",
                "code",
                "def add(a, b):\n    return a + b",
                "plain"),
            block => AssertBlock(
                block,
                3,
                "code",
                "algorithm",
                "Algorithm 1 Synthetic steps\n1: start\n2: stop",
                "plain"),
            block => AssertBlock(block, 3, "title", "heading-2", "Index", "plain"),
            block => AssertBlock(block, 3, "unknown", "index", "Alpha, 2\nBeta, 3", "plain"),
            block => AssertBlock(block, 3, "page-number", null, "3", "plain"));

        // Captions and footnotes stay on the figure, table, chart, or code Block they describe;
        // an empty array gives null, and every other Block has neither.
        (string?, string?) none = (null, null);
        Assert.Equal(
            [
                none, none, none, none, none, none, none,
                ("Figure 1 A synthetic figure", null),
                ("Table 1 Synthetic values", "Values are illustrative."),
                ("Chart 1 Synthetic growth", null),
                none, none, none,
                ("Listing 1 Synthetic code", null),
                none,
                none, none, none,
            ],
            first.Blocks.Select(block => (block.Caption, block.Footnote)));
        Assert.Equal(4, first.Blocks.Count(block => block.AssetId is not null));
        Assert.Contains("\"providerType\":\"mineru-cloud\"", first.ProviderMetadataJson);
        Assert.DoesNotContain("StorageRef", first.ProviderMetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(environment.Logger.Entries);

        var markdownArtifact = Assert.Single(
            first.Artifacts,
            artifact => artifact.Type == ArtifactTypes.Markdown);
        await using var markdownContent = await environment.Storage.OpenReadAsync(
            markdownArtifact.StorageRef,
            TestContext.Current.CancellationToken);
        using var reader = new StreamReader(markdownContent, Encoding.UTF8);
        Assert.Equal("# Chapter 1 Getting Started", await reader.ReadToEndAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Pipeline_fixture_fields_are_all_mapped_or_deliberately_ignored()
    {
        Assert.Empty(RecognizedAndIgnoredOverlap());
        Assert.Empty(FindUnaccountedFields(ReadPipelineFixture()));

        // The same check against a copy with one extra field proves the guard can fail.
        var refreshed = JsonNode.Parse(ReadPipelineFixture())!.AsArray();
        refreshed[0]!["table_summary"] = "synthetic";
        Assert.Equal(
            ["table_summary"],
            FindUnaccountedFields(Encoding.UTF8.GetBytes(refreshed.ToJsonString())));
    }

    [Fact]
    public async Task Keeps_reading_legacy_page_id_body_and_score_fields()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var contentList = """
            [
              {"type":"text","page_id":0,"text":"Heading","text_level":1,"bbox":[100,200,300,400],"score":0.95},
              {"type":"table","page_id":0,"body":"<table><tr><td>x</td></tr></table>"},
              {"type":"page_header","page_id":1,"text":"Running head"},
              {"type":"image","page_id":1,"img_path":"images/figure.png","bbox":[0.1,0.2,0.3,0.4]},
              {"type":"future_widget","page_id":1,"content":{"value":42}}
            ]
            """;
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "# Heading\n\n![figure](images/figure.png)"u8.ToArray()),
            ("content_list.json", Encoding.UTF8.GetBytes(contentList)),
            ("images/figure.png", PngBytes()));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive),
        TestContext.Current.CancellationToken);

        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
        Assert.Equal([1, 2], bundle.Pages.Select(page => page.Number));
        Assert.Equal("figure.png", Assert.Single(bundle.Assets).Name);
        Assert.Collection(
            bundle.Blocks,
            block =>
            {
                AssertBlock(block, 1, "title", "heading-1", "Heading", "plain");
                Assert.Equal(0.1, block.BoundingBox!.X0, 10);
                Assert.Equal(0.95, block.Confidence);
            },
            block => AssertBlock(block, 1, "table", null, "<table><tr><td>x</td></tr></table>", "html"),
            block => AssertBlock(block, 2, "header", null, "Running head", "plain"),
            block =>
            {
                AssertBlock(block, 2, "image", null, null, null);
                Assert.Equal(bundle.Assets[0].Id, block.AssetId);
                Assert.Equal(0.1, block.BoundingBox!.X0, 10);
            },
            block => AssertBlock(block, 2, "unknown", "future-widget", "{\"value\":42}", "plain"));
    }

    [Fact]
    public async Task Page_idx_is_authoritative_over_page_id()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            (
                "content_list.json",
                """
                [
                  {"type":"text","text":"zero","page_idx":0},
                  {"type":"text","text":"both","page_idx":2,"page_id":0},
                  {"type":"text","text":"legacy","page_id":1}
                ]
                """u8.ToArray()));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerULocal,
            archive),
        TestContext.Current.CancellationToken);

        Assert.Equal([1, 3, 2], bundle.Blocks.Select(block => block.PageNumber));
        Assert.Equal([1, 2, 3], bundle.Pages.Select(page => page.Number));
        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
    }

    [Theory]
    [InlineData("""{"type":"text","text":"x","page_idx":-1}""")]
    [InlineData("""{"type":"text","text":"x","page_idx":1.5}""")]
    [InlineData("""{"type":"text","text":"x","page_idx":null}""")]
    [InlineData("""{"type":"text","text":"x","page_idx":"0"}""")]
    [InlineData("""{"type":"text","text":"x","page_idx":-1,"page_id":0}""")]
    [InlineData("""{"type":"text","text":"x","page_idx":null,"page_id":0}""")]
    public async Task Rejects_an_invalid_page_idx_even_when_page_id_is_valid(string item)
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            ("content_list.json", Encoding.UTF8.GetBytes($"[{item}]")));

        var exception = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(new(
                parseRunId,
                ProviderTypes.MinerUCloud,
                archive),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-result-page-invalid", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
    }

    [Fact]
    public async Task Keeps_non_string_list_items_as_json_text()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            (
                "content_list.json",
                """[{"type":"list","list_items":["one",{"nested":2}],"page_idx":0}]"""u8.ToArray()));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive),
        TestContext.Current.CancellationToken);

        AssertBlock(
            Assert.Single(bundle.Blocks),
            1,
            "list",
            null,
            "[\"one\",{\"nested\":2}]",
            "plain");
    }

    [Fact]
    public async Task Keeps_caption_and_footnote_values_on_their_block_without_failing()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var contentList = """
            [
              {"type":"table","page_idx":0,"table_body":"<table></table>","table_caption":["Table 2","(continued)"],"table_footnote":["  ","Source: survey"]},
              {"type":"image","page_idx":0,"image_caption":[],"image_footnote":["   "]},
              {"type":"image","page_idx":0,"image_caption":null,"image_footnote":7},
              {"type":"image","page_idx":0,"image_caption":{"text":"x"},"image_footnote":true},
              {"type":"chart","page_idx":0,"chart_caption":"Chart 2 $x^2$","chart_footnote":"  "},
              {"type":"code","page_idx":0,"code_body":"x","code_caption":[1,"Listing 2",null,"",{"a":1}," indented "]},
              {"type":"figure","page_idx":0,"img_caption":["Legacy caption"],"img_footnote":["Legacy note"]},
              {"type":"image","page_idx":0,"image_caption":[],"img_caption":["Not read"],"img_footnote":["Legacy only"]},
              {"type":"image","page_idx":0,"table_caption":["Wrong type"],"code_footnote":["Wrong type"]},
              {"type":"text","page_idx":0,"text":"Body","image_caption":["Not a figure"],"table_footnote":["Not a table"]},
              {"type":"algorithm","page_idx":0,"code_body":"y","code_caption":["Algorithm 1"],"code_footnote":["Steps"]}
            ]
            """;
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            ("content_list.json", Encoding.UTF8.GetBytes(contentList)));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive),
        TestContext.Current.CancellationToken);

        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
        Assert.Equal(
            [
                ("Table 2\n(continued)", "Source: survey"),
                (null, null),
                (null, null),
                (null, null),
                ("Chart 2 $x^2$", null),
                ("Listing 2\n indented ", null),
                ("Legacy caption", "Legacy note"),
                (null, "Legacy only"),
                (null, null),
                (null, null),
                ("Algorithm 1", "Steps"),
            ],
            bundle.Blocks.Select(block => (block.Caption, block.Footnote)));
        // Annotations never become content or separate Blocks.
        Assert.Equal(11, bundle.Blocks.Count);
        Assert.Equal("<table></table>", bundle.Blocks[0].Content);
        Assert.Null(bundle.Blocks[1].Content);
        Assert.Equal("Body", bundle.Blocks[9].Content);
        Assert.Empty(environment.Logger.Entries);
    }

    [Fact]
    public async Task Warns_once_without_content_when_most_items_lack_a_page_field()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            (
                "content_list.json",
                """
                [
                  {"type":"text","text":"Private paragraph","page_idx":0},
                  {"type":"image","img_path":"images/private-figure.png","pg":3},
                  {"type":"table","table_body":"<table><tr><td>secret-cell</td></tr></table>"}
                ]
                """u8.ToArray()));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive),
        TestContext.Current.CancellationToken);

        Assert.Equal([1, null, null], bundle.Blocks.Select(block => block.PageNumber));
        var entry = Assert.Single(environment.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            new (string, object?)[]
            {
                ("ParseRunId", parseRunId),
                ("ProviderType", ProviderTypes.MinerUCloud),
                ("ItemsWithoutPage", 2),
                ("ItemCount", 3),
            },
            entry.Properties.Where(property => property.Key != "{OriginalFormat}")
                .Select(property => (property.Key, property.Value)));
        foreach (var forbidden in new[] { "Private paragraph", "private-figure", "images/", "secret-cell" })
        {
            Assert.DoesNotContain(forbidden, entry.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""[{"type":"text","text":"a","page_idx":0},{"type":"text","text":"b"}]""")]
    [InlineData("[]")]
    public async Task Does_not_warn_when_at_most_half_of_the_items_lack_a_page_field(string contentList)
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            ("content_list.json", Encoding.UTF8.GetBytes(contentList)));

        await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive),
        TestContext.Current.CancellationToken);

        Assert.Empty(environment.Logger.Entries);
    }

    [Fact]
    public async Task Allows_markdown_only_results_when_content_list_is_absent()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "plain markdown"u8.ToArray()));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerULocal,
            archive),
        TestContext.Current.CancellationToken);

        Assert.Empty(bundle.Pages);
        Assert.Empty(bundle.Blocks);
        Assert.Empty(bundle.Assets);
        Assert.Equal(2, bundle.Artifacts.Count);
        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
    }

    [Fact]
    public async Task Supports_the_observed_prefixed_and_nested_json_names()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            ("document_content_list.json", "[{\"type\":\"text\",\"text\":\"one\"}]"u8.ToArray()),
            ("output/layout.json", "{}"u8.ToArray()),
            ("output/model.json", "{}"u8.ToArray()));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive),
        TestContext.Current.CancellationToken);

        Assert.Single(bundle.Blocks);
        Assert.Contains(bundle.Artifacts, artifact => artifact.Type == ArtifactTypes.Layout);
        Assert.Contains(bundle.Artifacts, artifact => artifact.Type == ArtifactTypes.ModelOutput);
    }

    [Fact]
    public async Task Supports_official_local_nested_zip_layout_and_relative_image_paths()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("report/auto/report.md", "# Local result"u8.ToArray()),
            (
                "report/auto/report_content_list.json",
                "[{\"type\":\"image\",\"page_idx\":0,\"img_path\":\"images/figure.png\"}]"u8.ToArray()),
            ("report/auto/report_middle.json", "{}"u8.ToArray()),
            ("report/auto/report_model.json", "{}"u8.ToArray()),
            ("report/auto/images/figure.png", PngBytes()));

        var bundle = await environment.Normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerULocal,
            archive,
            Backend: "pipeline"),
        TestContext.Current.CancellationToken);

        var asset = Assert.Single(bundle.Assets);
        var block = Assert.Single(bundle.Blocks);
        Assert.Equal(asset.Id, block.AssetId);
        Assert.Equal(1, block.PageNumber);
        Assert.Contains(bundle.Artifacts, artifact => artifact.Type == ArtifactTypes.Markdown);
        Assert.Contains(bundle.Artifacts, artifact => artifact.Type == ArtifactTypes.ContentList);
        Assert.Contains(bundle.Artifacts, artifact => artifact.Type == ArtifactTypes.Layout);
        Assert.Contains(bundle.Artifacts, artifact => artifact.Type == ArtifactTypes.ModelOutput);
        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
    }

    [Theory]
    [InlineData("missing-markdown", "mineru-result-markdown-missing")]
    [InlineData("empty-markdown", "mineru-result-markdown-empty")]
    [InlineData("invalid-content-list", "mineru-result-json-invalid")]
    [InlineData("object-content-list", "mineru-result-content-list-invalid")]
    [InlineData("invalid-image", "mineru-result-image-unsupported")]
    [InlineData("invalid-page", "mineru-result-page-invalid")]
    public async Task Rejects_malformed_mineru_results_with_stable_codes(
        string scenario,
        string expectedCode)
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var entries = scenario switch
        {
            "missing-markdown" => new[]
            {
                ("content_list.json", "[]"u8.ToArray()),
            },
            "empty-markdown" => new[]
            {
                ("full.md", Array.Empty<byte>()),
            },
            "invalid-content-list" => new[]
            {
                ("full.md", "markdown"u8.ToArray()),
                ("content_list.json", "not-json"u8.ToArray()),
            },
            "object-content-list" => new[]
            {
                ("full.md", "markdown"u8.ToArray()),
                ("content_list.json", "{}"u8.ToArray()),
            },
            "invalid-image" => new[]
            {
                ("full.md", "markdown"u8.ToArray()),
                ("images/file.png", "not-an-image"u8.ToArray()),
            },
            "invalid-page" => new[]
            {
                ("full.md", "markdown"u8.ToArray()),
                ("content_list.json", "[{\"type\":\"text\",\"page_id\":-1}]"u8.ToArray()),
            },
            _ => throw new InvalidOperationException(),
        };
        var archive = await environment.StoreArchiveAsync(parseRunId, entries);

        var exception = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(new(
                parseRunId,
                ProviderTypes.MinerUCloud,
                archive),
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task Rejects_ambiguous_json_artifacts_deterministically()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()),
            ("one_content_list.json", "[]"u8.ToArray()),
            ("two_content_list.json", "[]"u8.ToArray()));

        var exception = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(new(
                parseRunId,
                ProviderTypes.MinerUCloud,
                archive),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-result-entry-ambiguous", exception.ErrorCode);
    }

    [Fact]
    public async Task Rejects_ambiguous_nested_markdown_and_image_aliases()
    {
        using var environment = new NormalizerTestEnvironment();
        var markdownParseRunId = Guid.NewGuid();
        var markdownArchive = await environment.StoreArchiveAsync(
            markdownParseRunId,
            ("one/result.md", "one"u8.ToArray()),
            ("two/result.md", "two"u8.ToArray()));

        var markdownException = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(new(
                markdownParseRunId,
                ProviderTypes.MinerULocal,
                markdownArchive),
            TestContext.Current.CancellationToken));
        Assert.Equal("mineru-result-entry-ambiguous", markdownException.ErrorCode);

        var imageParseRunId = Guid.NewGuid();
        var imageArchive = await environment.StoreArchiveAsync(
            imageParseRunId,
            ("full.md", "markdown"u8.ToArray()),
            ("one/images/figure.png", PngBytes()),
            ("two/images/figure.png", PngBytes()));

        var imageException = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(new(
                imageParseRunId,
                ProviderTypes.MinerULocal,
                imageArchive),
            TestContext.Current.CancellationToken));
        Assert.Equal("mineru-result-entry-ambiguous", imageException.ErrorCode);
    }

    [Fact]
    public async Task Enforces_markdown_limit_before_persisting_a_bundle()
    {
        using var environment = new NormalizerTestEnvironment(
            normalizationOptions: new ProviderResultNormalizationOptions
            {
                MaxMarkdownBytes = 4,
                MaxJsonBytes = 1024,
                MaxAssetBytes = 1024,
                TemporaryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            });
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "too long"u8.ToArray()));

        var exception = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(new(
                parseRunId,
                ProviderTypes.MinerUCloud,
                archive),
            TestContext.Current.CancellationToken));

        Assert.Equal("mineru-result-markdown-too-large", exception.ErrorCode);
    }

    [Fact]
    public async Task Revalidates_the_intake_manifest_before_reading_entries()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()));
        var changedManifest = archive with { Entries = [] };

        var exception = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(new(
                parseRunId,
                ProviderTypes.MinerUCloud,
                changedManifest),
            TestContext.Current.CancellationToken));

        Assert.Equal("provider-result-archive-changed", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Security, exception.Category);
    }

    [Fact]
    public async Task Non_seekable_storage_uses_and_removes_a_temporary_archive()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "markdown"u8.ToArray()));
        var temporaryPath = Path.Combine(environment.RootPath, "normalizer-temp");
        var normalizer = new MinerUResultNormalizer(
            new NonSeekableReadStorage(environment.Storage),
            new ProviderResultNormalizationOptions { TemporaryPath = temporaryPath },
            new CapturingLogger());

        var bundle = await normalizer.NormalizeAsync(new(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive),
        TestContext.Current.CancellationToken);

        Assert.Equal(2, bundle.Artifacts.Count);
        Assert.Empty(Directory.GetFiles(temporaryPath, "*", SearchOption.AllDirectories));
    }

    private static byte[] PngBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static byte[] JpegBytes() =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static byte[] ReadPipelineFixture() =>
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MinerU",
            "pipeline_content_list.json"));

    private static string[] PipelineFixtureImagePaths(byte[] contentList)
    {
        using var document = JsonDocument.Parse(contentList);
        return document.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("img_path", out _))
            .Select(item => item.GetProperty("img_path").GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<string> RecognizedAndIgnoredOverlap() =>
        MinerUResultNormalizer.RecognizedContentListFields
            .Intersect(IgnoredContentListFields, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> FindUnaccountedFields(byte[] contentList)
    {
        using var document = JsonDocument.Parse(contentList);
        return document.RootElement.EnumerateArray()
            .SelectMany(item => item.EnumerateObject().Select(property => property.Name))
            .Where(name =>
                !MinerUResultNormalizer.RecognizedContentListFields.Contains(name)
                && !IgnoredContentListFields.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertBlock(
        ParseBlock block,
        int? pageNumber,
        string type,
        string? subtype,
        string? content,
        string? contentFormat)
    {
        Assert.Equal(pageNumber, block.PageNumber);
        Assert.Equal(type, block.Type);
        Assert.Equal(subtype, block.Subtype);
        Assert.Equal(content, block.Content);
        Assert.Equal(contentFormat, block.ContentFormat);
    }

    private sealed class NormalizerTestEnvironment : IDisposable
    {
        public NormalizerTestEnvironment(
            ProviderResultNormalizationOptions? normalizationOptions = null)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "structadoc-mineru-normalizer-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            Storage = new LocalFileStorage(new FileStorageOptions { RootPath = RootPath });
            Intake = new StoredProviderResultIntake(
                Storage,
                new ProviderResultIntakeOptions
                {
                    MaxArchiveBytes = 1024 * 1024,
                    MaxEntryCount = 100,
                    MaxEntryBytes = 1024 * 1024,
                    MaxExpandedBytes = 2 * 1024 * 1024,
                    MaxCompressionRatio = 1000,
                    TemporaryPath = Path.Combine(RootPath, "intake-temp"),
                });
            Normalizer = new MinerUResultNormalizer(
                Storage,
                normalizationOptions ?? new ProviderResultNormalizationOptions
                {
                    MaxMarkdownBytes = 1024 * 1024,
                    MaxJsonBytes = 1024 * 1024,
                    MaxAssetBytes = 1024 * 1024,
                    TemporaryPath = Path.Combine(RootPath, "normalizer-temp"),
                },
                Logger);
        }

        public string RootPath { get; }

        public CapturingLogger Logger { get; } = new();

        public LocalFileStorage Storage { get; }

        public StoredProviderResultIntake Intake { get; }

        public MinerUResultNormalizer Normalizer { get; }

        public async Task<StoredProviderArchive> StoreArchiveAsync(
            Guid parseRunId,
            params (string Path, byte[] Content)[] entries)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in entries)
                {
                    var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
                    await using var entryContent = entry.Open();
                    await entryContent.WriteAsync(content);
                }
            }

            await using var result = new ProviderResultContent(
                new MemoryStream(output.ToArray(), writable: false),
                "application/zip");
            return await Intake.StoreArchiveAsync(parseRunId, result);
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Properties);

    private sealed class CapturingLogger : ILogger<MinerUResultNormalizer>
    {
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (entries)
                {
                    return entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            lock (entries)
            {
                entries.Add(new LogEntry(logLevel, formatter(state, exception), properties.ToArray()));
            }
        }
    }

    private sealed class NonSeekableReadStorage(IFileStorage inner) : IFileStorage
    {
        public Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(storageRef, content, maxBytes, cancellationToken);

        public async Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default) =>
            new NonSeekableReadStream(await inner.OpenReadAsync(storageRef, cancellationToken));

        public Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default) =>
            inner.DeleteIfExistsAsync(storageRef, cancellationToken);
    }

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
