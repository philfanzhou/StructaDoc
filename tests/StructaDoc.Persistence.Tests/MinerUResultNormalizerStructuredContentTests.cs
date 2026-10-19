using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

namespace StructaDoc.Persistence.Tests;

public sealed partial class MinerUResultNormalizerTests
{
    [Fact]
    public async Task Normalizes_the_unmodified_real_mineru_4_html_archive_idempotently()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        await using var content = new ProviderResultContent(
            File.OpenRead(MinerUFixturePath("mineru-4.0.12-html.zip")), "application/zip");
        var archive = await environment.Intake.StoreArchiveAsync(parseRunId, content, TestContext.Current.CancellationToken);
        var request = new ProviderResultNormalizationRequest(parseRunId, ProviderTypes.MinerULocal, archive);
        var bundle = await environment.Normalizer.NormalizeAsync(request, TestContext.Current.CancellationToken);
        var replay = await environment.Normalizer.NormalizeAsync(request, TestContext.Current.CancellationToken);

        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
        Assert.Equal(ParseBundleValidator.ComputeFingerprint(bundle), ParseBundleValidator.ComputeFingerprint(replay));
        Assert.Equal(bundle.Blocks.Select(block => block.Id), replay.Blocks.Select(block => block.Id));
        Assert.Equal(bundle.Artifacts.Select(artifact => artifact.Id), replay.Artifacts.Select(artifact => artifact.Id));
        Assert.Empty(bundle.Pages);
        Assert.Empty(bundle.Assets);
        Assert.Equal(Enumerable.Range(0, 6), bundle.Blocks.Select(block => block.Sequence));
        Assert.Collection(bundle.Blocks,
            block => AssertBlock(block, null, "title", "heading-1", "StructaDoc protocol probe", "markdown"),
            block => AssertBlock(block, null, "text", null, "Public synthetic text.", "markdown"),
            block => AssertBlock(block, null, "title", "heading-2", "Section two", "markdown"),
            block => AssertBlock(block, null, "list", null, "- First item\n- Second item", "markdown"),
            block => AssertBlock(block, null, "table", null, "| Name | Value |\n| --- | --- |\n| A | 1 |", "markdown"),
            block => AssertBlock(block, null, "code", "code", "```lua\nprint(1)\n```", "markdown"));
        Assert.All(bundle.Blocks, block =>
        {
            Assert.Equal("{\"providerPageId\":0}", block.SourceLocatorJson);
            Assert.Null(block.ProviderDataJson);
            Assert.Null(block.Caption);
            Assert.Null(block.Footnote);
        });
        Assert.Equal(
            ["layout:layout.json", "markdown:document.md", "model-output:model-output.json", "provider-archive:provider-result.zip", "provider-raw:structured-content.json"],
            bundle.Artifacts.Select(artifact => $"{artifact.Type}:{artifact.Name}").Order(StringComparer.Ordinal));
        Assert.Equal("{\"providerType\":\"mineru-local\"}", bundle.ProviderMetadataJson);
        Assert.Empty(environment.Logger.Entries);
    }

    [Fact]
    public async Task Maps_every_physical_v4_block_and_retains_empty_declared_pages()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(parseRunId,
            ("markdown.md", "# Synthetic document"u8.ToArray()),
            ("structured_content.json", File.ReadAllBytes(MinerUFixturePath("mineru-4-physical-structured-content.json"))),
            ("middle_json.json", "{\"schema\":\"docvortex.middle\",\"schema_version\":\"2.0\"}"u8.ToArray()),
            ("model_output.json", "{}"u8.ToArray()),
            ("images/table.png", PngBytes()), ("images/formula.png", PngBytes()),
            ("images/figure.png", PngBytes()), ("images/chart.png", PngBytes()));
        var bundle = await environment.Normalizer.NormalizeAsync(new(parseRunId, ProviderTypes.MinerULocal, archive), TestContext.Current.CancellationToken);
        var replay = await environment.Normalizer.NormalizeAsync(new(parseRunId, ProviderTypes.MinerULocal, archive), TestContext.Current.CancellationToken);

        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
        Assert.Equal(ParseBundleValidator.ComputeFingerprint(bundle), ParseBundleValidator.ComputeFingerprint(replay));
        Assert.Equal([3, 5], bundle.Pages.Select(page => page.Number));
        Assert.Equal(["{\"providerPageId\":2}", "{\"providerPageId\":4}"], bundle.Pages.Select(page => page.SourceLocatorJson));
        Assert.All(bundle.Pages, page => { Assert.Null(page.Width); Assert.Null(page.Height); Assert.Null(page.Unit); });
        Assert.Equal(Enumerable.Range(0, 18), bundle.Blocks.Select(block => block.Sequence));
        Assert.Collection(bundle.Blocks,
            block => AssertBlock(block, 3, "title", "heading-1", "Synthetic document", "markdown"),
            block => AssertBlock(block, 3, "title", "heading-99", "Synthetic section", "markdown"),
            block => AssertBlock(block, 3, "text", null, "A **synthetic** paragraph.", "markdown"),
            block => AssertBlock(block, 3, "text", "ref-text", "[1] Synthetic reference", "markdown"),
            block => AssertBlock(block, 3, "list", null, "- First\n- Second", "markdown"),
            block => AssertBlock(block, 3, "table", null, "| Name | Value |\n| --- | --- |\n| A | 1 |", "markdown"),
            block => AssertBlock(block, 3, "formula", null, "E = m c^2", "latex"),
            block => AssertBlock(block, 3, "image", null, "![synthetic](images/figure.png)", "markdown"),
            block => AssertBlock(block, 3, "image", "chart", "![chart](images/chart.png)", "markdown"),
            block => AssertBlock(block, 3, "code", "code", "```python\nprint(1)\n```", "markdown"),
            block => AssertBlock(block, 3, "code", "algorithm", "1. Start\n2. Stop", "markdown"),
            block => AssertBlock(block, 3, "header", null, "Synthetic header", "markdown"),
            block => AssertBlock(block, 3, "footer", null, "Synthetic footer", "markdown"),
            block => AssertBlock(block, 3, "page-number", null, "3", "markdown"),
            block => AssertBlock(block, 3, "footnote", null, "Synthetic page footnote", "markdown"),
            block => AssertBlock(block, 3, "unknown", "index", "Alpha, 3", "markdown"),
            block => AssertBlock(block, 3, "unknown", "aside-text", "Synthetic margin", "markdown"),
            block => AssertBlock(block, 3, "unknown", "future-widget", "Future content", "markdown"));
        Assert.Equal(new NormalizedBoundingBox(.1, .1, .9, .2), bundle.Blocks[0].BoundingBox);
        Assert.Equal(new NormalizedBoundingBox(.2, .3, .8, .4), bundle.Blocks[6].BoundingBox);
        Assert.Equal(new NormalizedBoundingBox(.1, .4, .9, .7), bundle.Blocks[7].BoundingBox);
        Assert.All(bundle.Blocks.Where((_, i) => i is not (0 or 6 or 7)), block => Assert.Null(block.BoundingBox));
        var names = new[] { "table.png", "formula.png", "figure.png", "chart.png" };
        for (var index = 0; index < names.Length; index++)
        {
            Assert.Equal(Assert.Single(bundle.Assets, asset => asset.Name == names[index]).Id, bundle.Blocks[index + 5].AssetId);
        }
        Assert.All(bundle.Blocks.Where((_, i) => i is < 5 or > 8), block => Assert.Null(block.AssetId));
        Assert.Equal((" Table 1 \ncontinued", "Synthetic table note"), (bundle.Blocks[5].Caption, bundle.Blocks[5].Footnote));
        Assert.Equal(("Figure 1", "Figure note"), (bundle.Blocks[7].Caption, bundle.Blocks[7].Footnote));
        Assert.Equal(("Chart 1", null), (bundle.Blocks[8].Caption, bundle.Blocks[8].Footnote));
        Assert.Equal(("Listing 1", "Code note"), (bundle.Blocks[9].Caption, bundle.Blocks[9].Footnote));
        Assert.All(bundle.Blocks.Where((_, i) => i is not (5 or 7 or 8 or 9)), block => { Assert.Null(block.Caption); Assert.Null(block.Footnote); });
        Assert.All(bundle.Blocks, block =>
        {
            Assert.Equal("{\"providerPageId\":2}", block.SourceLocatorJson);
            Assert.Null(block.ProviderDataJson);
            Assert.Null(block.Confidence);
        });
        Assert.DoesNotContain("synthetic-raw-only", bundle.ProviderMetadataJson);
        Assert.DoesNotContain("raw-only", bundle.ProviderMetadataJson);
    }

    [Theory]
    [InlineData("html", "logical", false)]
    [InlineData("xlsx", "sheet", false)]
    [InlineData("pptx", "slide", false)]
    [InlineData("epub", "spine", false)]
    [InlineData("docx", "declared", false)]
    [InlineData("unknown", "unknown", false)]
    [InlineData(null, null, false)]
    [InlineData("pdf", "logical", true)]
    [InlineData("ofd", null, true)]
    [InlineData("docx", "physical", true)]
    public async Task Uses_only_reliable_physical_page_identity(string? suffix, string? kind, bool physical)
    {
        var root = JsonNode.Parse(StructuredDocument("{\"type\":\"text\",\"content\":\"x\"}"))!;
        root["metadata"] = new JsonObject { ["file_suffix"] = suffix, ["document"] = new JsonObject { ["page_count_kind"] = kind } };
        using var environment = new NormalizerTestEnvironment();
        var bundle = await NormalizeStructuredAsync(environment, root.ToJsonString());
        var block = Assert.Single(bundle.Blocks);
        Assert.Equal(physical ? 3 : (int?)null, block.PageNumber);
        Assert.Equal("{\"providerPageId\":2}", block.SourceLocatorJson);
        Assert.Equal(physical ? 1 : 0, bundle.Pages.Count);
    }

    [Theory]
    [InlineData("{sensitive", "mineru-result-json-invalid")]
    [InlineData("[]", "mineru-result-structured-content-invalid")]
    [InlineData("{}", "mineru-result-structured-content-invalid")]
    [InlineData("{\"pages\":null}", "mineru-result-structured-content-invalid")]
    [InlineData("{\"pages\":[null]}", "mineru-result-structured-content-invalid")]
    [InlineData("{\"pages\":[{\"page_idx\":0}]}", "mineru-result-structured-content-invalid")]
    [InlineData("{\"pages\":[{\"page_idx\":0,\"blocks\":{}}]}", "mineru-result-structured-content-invalid")]
    [InlineData("{\"pages\":[{\"page_idx\":0,\"blocks\":[null]}]}", "mineru-result-block-invalid")]
    public async Task Rejects_present_malformed_v4_structure(string json, string code)
    {
        using var environment = new NormalizerTestEnvironment();
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(environment, json));
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, error.Category);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Empty(environment.Logger.Entries);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":null,\"content\":\"sensitive\"}")]
    [InlineData("{\"type\":12,\"content\":\"sensitive\"}")]
    [InlineData("{\"type\":\" \",\"content\":\"sensitive\"}")]
    [InlineData("{\"type\":\"text\"}")]
    [InlineData("{\"type\":\"text\",\"content\":null}")]
    [InlineData("{\"type\":\"text\",\"content\":{\"sensitive\":true}}")]
    [InlineData("{\"type\":\"text\",\"content\":[]}")]
    public async Task Rejects_invalid_v4_type_and_content_without_logging_values(string block)
    {
        using var environment = new NormalizerTestEnvironment();
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(environment, StructuredDocument(block)));
        Assert.Equal("mineru-result-block-invalid", error.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, error.Category);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Empty(environment.Logger.Entries);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("\"2\"")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    public async Task Rejects_invalid_v4_page_indices(string pageIndex)
    {
        using var environment = new NormalizerTestEnvironment();
        var json = $"{{\"pages\":[{{\"page_idx\":{pageIndex},\"blocks\":[]}}]}}";
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(environment, json));
        Assert.Equal("mineru-result-page-invalid", error.ErrorCode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task Rejects_repeated_or_unordered_v4_page_indices(int second)
    {
        using var environment = new NormalizerTestEnvironment();
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(environment,
            $"{{\"pages\":[{{\"page_idx\":2,\"blocks\":[]}},{{\"page_idx\":{second},\"blocks\":[]}}]}}"));
        Assert.Equal("mineru-result-page-invalid", error.ErrorCode);
    }

    [Theory]
    [InlineData("[100,200,300,400]")]
    [InlineData("[0.8,0.1,0.2,0.9]")]
    [InlineData("[-0.1,0,1,1]")]
    [InlineData("[0,0,1.1,1]")]
    [InlineData("[0,0,1]")]
    [InlineData("[0,0,\"1\",1]")]
    [InlineData("[0,0,1e400,1]")]
    [InlineData("null")]
    public async Task Does_not_scale_or_invent_v4_bounding_boxes(string bbox)
    {
        using var environment = new NormalizerTestEnvironment();
        var bundle = await NormalizeStructuredAsync(environment, StructuredDocument($"{{\"type\":\"text\",\"content\":\"x\",\"bbox\":{bbox}}}"));
        Assert.Null(Assert.Single(bundle.Blocks).BoundingBox);
    }

    [Theory]
    [InlineData("https://example.invalid/images/figure.png")]
    [InlineData("//example.invalid/images/figure.png")]
    [InlineData("file:///images/figure.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/images/figure.png")]
    [InlineData("../images/figure.png")]
    [InlineData("images/../images/figure.png")]
    [InlineData("images\\figure.png")]
    [InlineData("images/missing.png")]
    [InlineData("images/%2e%2e/figure.png")]
    public async Task Never_resolves_external_unsafe_or_missing_v4_image_sources(string source)
    {
        using var environment = new NormalizerTestEnvironment();
        var block = JsonSerializer.Serialize(new { type = "image", content = "", image_source = source });
        var bundle = await NormalizeStructuredAsync(environment, StructuredDocument(block), [("images/figure.png", PngBytes())]);
        Assert.Null(Assert.Single(bundle.Blocks).AssetId);
        Assert.Null(bundle.Blocks[0].ContentFormat);
        Assert.Single(bundle.Assets);
    }

    [Fact]
    public async Task Reads_v4_annotation_object_content_only_for_visual_blocks()
    {
        using var environment = new NormalizerTestEnvironment();
        var bundle = await NormalizeStructuredAsync(environment, StructuredDocument("""
            {"type":"image","content":"x","captions":[{"content":" first "},null,"wrong",{}, {"content":2},{"content":" "},{"content":"second"}],"footnotes":{"content":"ignored"}},
            {"type":"table","content":"x","captions":null,"footnotes":[{"content":"note"}]},
            {"type":"code","content":"x","sub_type":"invalid token","captions":true,"footnotes":5},
            {"type":"text","content":"x","captions":[{"content":"ignored"}]}
            """));
        Assert.Equal([(" first \nsecond", (string?)null), (null, "note"), (null, null), (null, null)], bundle.Blocks.Select(block => (block.Caption, block.Footnote)));
        Assert.All(bundle.Blocks, block => Assert.Equal("x", block.Content));
        Assert.Null(bundle.Blocks[2].Subtype);
    }

    [Theory]
    [InlineData("structured_content.json")]
    [InlineData("middle_json.json")]
    [InlineData("model_output.json")]
    public async Task Rejects_same_priority_v4_candidates_in_both_archive_orders(string name)
    {
        foreach (var reverse in new[] { false, true })
        {
            using var environment = new NormalizerTestEnvironment();
            var entries = new[] { ($"one/{name}", "{}"u8.ToArray()), ($"two/{name}", "{}"u8.ToArray()) };
            if (reverse) { Array.Reverse(entries); }
            var extra = name == "structured_content.json" ? entries : [("structured_content.json", Encoding.UTF8.GetBytes(StructuredDocument(""))), .. entries];
            var archive = await environment.StoreArchiveAsync(Guid.NewGuid(), [("markdown.md", "markdown"u8.ToArray()), .. extra]);
            var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => environment.Normalizer.NormalizeAsync(new(Guid.NewGuid(), ProviderTypes.MinerULocal, archive), TestContext.Current.CancellationToken));
            Assert.Equal("mineru-result-entry-ambiguous", error.ErrorCode);
        }
    }

    [Fact]
    public async Task Root_v4_names_win_and_unique_nested_entries_resolve_validated_asset_aliases()
    {
        using var environment = new NormalizerTestEnvironment();
        var json = StructuredDocument("{\"type\":\"image\",\"content\":\"figure\",\"image_source\":\"images/figure.png\"}");
        var bundle = await NormalizeStructuredAsync(environment, json,
            [("nested/structured_content.json", "invalid"u8.ToArray()), ("middle_json.json", "{}"u8.ToArray()), ("nested/middle_json.json", "invalid"u8.ToArray()),
             ("model_output.json", "{}"u8.ToArray()), ("nested/model_output.json", "invalid"u8.ToArray()), ("nested/extra.md", "extra"u8.ToArray()), ("nested/images/figure.png", PngBytes())]);
        Assert.Equal(Assert.Single(bundle.Assets).Id, Assert.Single(bundle.Blocks).AssetId);
        var id = Guid.NewGuid();
        var nestedArchive = await environment.StoreArchiveAsync(id,
            ("output/markdown.md", "nested"u8.ToArray()), ("output/structured_content.json", Encoding.UTF8.GetBytes(json)),
            ("output/middle_json.json", "{}"u8.ToArray()), ("output/model_output.json", "{}"u8.ToArray()), ("output/images/figure.png", PngBytes()));
        var nested = await environment.Normalizer.NormalizeAsync(new(id, ProviderTypes.MinerULocal, nestedArchive), TestContext.Current.CancellationToken);
        Assert.Equal(Assert.Single(nested.Assets).Id, Assert.Single(nested.Blocks).AssetId);
        Assert.Equal(5, nested.Artifacts.Count);
    }

    [Fact]
    public async Task Legacy_pipeline_bundle_keeps_its_baseline_fingerprint()
    {
        using var environment = new NormalizerTestEnvironment();
        var id = Guid.Parse("d54cc20e-d1de-46de-8978-8377590bbc5a");
        await using var content = new ProviderResultContent(
            File.OpenRead(MinerUFixturePath("pipeline-legacy-baseline.zip")), "application/zip");
        var archive = await environment.Intake.StoreArchiveAsync(id, content, TestContext.Current.CancellationToken);
        var bundle = await environment.Normalizer.NormalizeAsync(new(id, ProviderTypes.MinerUCloud, archive, "pipeline"), TestContext.Current.CancellationToken);
        // Captured with the unchanged mainline normalizer and this fixed ZIP/run ID.
        Assert.Equal("1e15d27b17be8edec0d6887616abc514b6da75209845213e950db0afd7cbba05", ParseBundleValidator.ComputeFingerprint(bundle));
    }

    [Fact]
    public async Task Rejects_v4_ambiguous_validated_image_aliases()
    {
        using var environment = new NormalizerTestEnvironment();
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(environment,
            StructuredDocument("{\"type\":\"image\",\"content\":\"x\",\"image_source\":\"images/figure.png\"}"),
            [("one/images/figure.png", PngBytes()), ("two/images/figure.png", PngBytes())]));
        Assert.Equal("mineru-result-entry-ambiguous", error.ErrorCode);
    }

    [Fact]
    public async Task Legacy_content_list_keeps_precedence_and_identical_derived_fingerprint()
    {
        using var environment = new NormalizerTestEnvironment();
        var id = Guid.NewGuid();
        (string Path, byte[] Content)[] entries = [("full.md", "legacy"u8.ToArray()), ("content_list.json", ReadPipelineFixture()), .. PipelineFixtureImagePaths(ReadPipelineFixture()).Select(path => (path, JpegBytes()))];
        var firstArchive = await environment.StoreArchiveAsync(id, entries);
        var first = await environment.Normalizer.NormalizeAsync(new(id, ProviderTypes.MinerUCloud, firstArchive), TestContext.Current.CancellationToken);
        // Separate intake identity permits a second ZIP; the normalizer still uses the same run and derived keys.
        var mixedArchive = await environment.StoreArchiveAsync(Guid.NewGuid(), [.. entries, ("one/structured_content.json", "invalid"u8.ToArray()), ("two/structured_content.json", "invalid"u8.ToArray())]);
        var mixed = await environment.Normalizer.NormalizeAsync(new(id, ProviderTypes.MinerUCloud, mixedArchive), TestContext.Current.CancellationToken);
        Assert.Equal(first.Blocks, mixed.Blocks);
        Assert.Equal(first.Pages, mixed.Pages);
        Assert.Equal(first.Assets, mixed.Assets);
        var mixedWithSameArchive = mixed with { Artifacts = mixed.Artifacts.Select(artifact => artifact.Type == ArtifactTypes.ProviderArchive ? first.Artifacts.Single(item => item.Type == ArtifactTypes.ProviderArchive) : artifact).ToArray() };
        Assert.Equal(ParseBundleValidator.ComputeFingerprint(first), ParseBundleValidator.ComputeFingerprint(mixedWithSameArchive));
    }

    [Fact]
    public async Task Enforces_v4_json_page_block_and_content_limits()
    {
        using var small = new NormalizerTestEnvironment(new ProviderResultNormalizationOptions { MaxJsonBytes = 8 });
        var jsonError = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(small, StructuredDocument("")));
        Assert.Equal("mineru-result-json-too-large", jsonError.ErrorCode);
        using var large = new NormalizerTestEnvironment(maxEntryBytes: 8 * 1024 * 1024, maxCompressionRatio: 10000);
        var pageJson = JsonSerializer.Serialize(new { pages = Enumerable.Range(0, ParseBundleValidator.MaxPages + 1).Select(i => new { page_idx = i, blocks = Array.Empty<object>() }) });
        var pageError = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(large, pageJson));
        Assert.Equal("mineru-result-page-limit", pageError.ErrorCode);
        var blockJson = StructuredDocument(string.Join(',', Enumerable.Repeat("{\"type\":\"text\",\"content\":\"\"}", ParseBundleValidator.MaxBlocks + 1)));
        var blockError = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(large, blockJson));
        Assert.Equal("mineru-result-block-limit", blockError.ErrorCode);
        foreach (var field in new[] { "content", "caption", "footnote" })
        {
            var source = new JsonObject { ["type"] = "table", ["content"] = "x" };
            var oversized = new string('x', ParseBundleValidator.MaxBlockContentCharacters + 1);
            if (field == "content") { source["content"] = oversized; }
            else { source[field == "caption" ? "captions" : "footnotes"] = new JsonArray(new JsonObject { ["content"] = oversized }); }
            var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => NormalizeStructuredAsync(large, StructuredDocument(source.ToJsonString())));
            Assert.Equal("mineru-result-bundle-invalid", error.ErrorCode);
            Assert.DoesNotContain(oversized[..100], error.Message);
        }
    }

    [Fact]
    public async Task V4_storage_conflicts_io_and_cancellation_keep_existing_categories()
    {
        using var environment = new NormalizerTestEnvironment();
        var id = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(id, ("markdown.md", "markdown"u8.ToArray()), ("structured_content.json", Encoding.UTF8.GetBytes(StructuredDocument(""))));
        foreach (var exception in new Exception[] { new IOException("synthetic"), new StorageObjectConflictException("synthetic") })
        {
            var normalizer = new MinerUResultNormalizer(new FailingDerivedStorage(environment.Storage, exception), new ProviderResultNormalizationOptions(), new CapturingLogger());
            var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() => normalizer.NormalizeAsync(new(id, ProviderTypes.MinerULocal, archive), TestContext.Current.CancellationToken));
            Assert.Equal(exception is IOException ? ProviderFailureCategory.Transient : ProviderFailureCategory.Permanent, error.Category);
            Assert.Equal(exception is IOException ? "provider-result-normalization-io-failed" : "provider-result-derived-storage-conflict", error.ErrorCode);
        }
        var canceledNormalizer = new MinerUResultNormalizer(new FailingDerivedStorage(environment.Storage, new OperationCanceledException()), new ProviderResultNormalizationOptions(), new CapturingLogger());
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceledNormalizer.NormalizeAsync(new(id, ProviderTypes.MinerULocal, archive), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_v4_runs_have_disjoint_ids_and_storage_keys()
    {
        using var environment = new NormalizerTestEnvironment();
        var json = StructuredDocument("{\"type\":\"image\",\"content\":\"x\",\"image_source\":\"images/figure.png\"}");
        var bundles = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NormalizeStructuredAsync(environment, json, [("images/figure.png", PngBytes())])));
        Assert.Equal(4, bundles.SelectMany(bundle => bundle.Blocks).Select(block => block.Id).Distinct().Count());
        Assert.Equal(4, bundles.SelectMany(bundle => bundle.Assets).Select(asset => asset.Id).Distinct().Count());
        Assert.Equal(4, bundles.SelectMany(bundle => bundle.Assets).Select(asset => asset.StorageRef).Distinct().Count());
        Assert.Equal(12, bundles.SelectMany(bundle => bundle.Artifacts).Select(artifact => artifact.Id).Distinct().Count());
        Assert.Equal(12, bundles.SelectMany(bundle => bundle.Artifacts).Select(artifact => artifact.StorageRef).Distinct().Count());
    }

    private static string MinerUFixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "MinerU", name);

    private static string StructuredDocument(string blocks) => $"{{\"pages\":[{{\"page_idx\":2,\"blocks\":[{blocks}]}}]}}";

    private static async Task<ParseBundle> NormalizeStructuredAsync(NormalizerTestEnvironment environment, string json, (string Path, byte[] Content)[]? extra = null)
    {
        var id = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(id, [("markdown.md", "markdown"u8.ToArray()), ("structured_content.json", Encoding.UTF8.GetBytes(json)), .. extra ?? []]);
        return await environment.Normalizer.NormalizeAsync(new(id, ProviderTypes.MinerULocal, archive), TestContext.Current.CancellationToken);
    }

    private sealed class FailingDerivedStorage(IFileStorage inner, Exception exception) : IFileStorage
    {
        public Task<StoredFile> WriteAsync(string storageRef, Stream content, long maxBytes, CancellationToken cancellationToken = default) => Task.FromException<StoredFile>(exception);
        public Task<Stream> OpenReadAsync(string storageRef, CancellationToken cancellationToken = default) => inner.OpenReadAsync(storageRef, cancellationToken);
        public Task DeleteIfExistsAsync(string storageRef, CancellationToken cancellationToken = default) => inner.DeleteIfExistsAsync(storageRef, cancellationToken);
    }
}
