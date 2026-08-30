using System.IO.Compression;
using System.Text;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

namespace StructaDoc.Persistence.Tests;

public sealed class MinerUResultNormalizerTests
{
    [Fact]
    public async Task Normalizes_verified_mineru_artifacts_assets_and_blocks_idempotently()
    {
        using var environment = new NormalizerTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var contentList = """
            [
              {"type":"text","page_id":0,"text":"Heading","text_level":1,"bbox":[100,200,300,400],"score":0.95},
              {"type":"table","page_id":0,"body":"<table><tr><td>x</td></tr></table>"},
              {"type":"equation","page_id":1,"text":"E=mc^2"},
              {"type":"image","page_id":1,"img_path":"images/figure.png","bbox":[0.1,0.2,0.3,0.4]},
              {"type":"future_widget","content":{"value":42}}
            ]
            """;
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "# Heading\n\n![figure](images/figure.png)"u8.ToArray()),
            ("content_list.json", Encoding.UTF8.GetBytes(contentList)),
            ("content_list_v2.json", "[]"u8.ToArray()),
            ("layout.json", "{}"u8.ToArray()),
            ("model.json", "{}"u8.ToArray()),
            ("images/figure.png", PngBytes()));
        var request = new ProviderResultNormalizationRequest(
            parseRunId,
            ProviderTypes.MinerUCloud,
            archive,
            "vlm",
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
        Assert.Equal([1, 2], first.Pages.Select(page => page.Number));
        Assert.Single(first.Assets);
        Assert.Equal("image/png", first.Assets[0].MediaType);
        Assert.Equal("figure.png", first.Assets[0].Name);
        Assert.Equal(6, first.Artifacts.Count);
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.ProviderArchive);
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.Markdown);
        Assert.Equal(2, first.Artifacts.Count(artifact => artifact.Type == ArtifactTypes.ContentList));
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.Layout);
        Assert.Contains(first.Artifacts, artifact => artifact.Type == ArtifactTypes.ModelOutput);

        Assert.Collection(
            first.Blocks,
            block =>
            {
                Assert.Equal("title", block.Type);
                Assert.Equal("heading-1", block.Subtype);
                Assert.Equal("plain", block.ContentFormat);
                Assert.Equal(0.1, block.BoundingBox!.X0, 10);
                Assert.Equal(0.95, block.Confidence);
            },
            block =>
            {
                Assert.Equal("table", block.Type);
                Assert.Equal("html", block.ContentFormat);
            },
            block =>
            {
                Assert.Equal("formula", block.Type);
                Assert.Equal("latex", block.ContentFormat);
            },
            block =>
            {
                Assert.Equal("image", block.Type);
                Assert.Equal(first.Assets[0].Id, block.AssetId);
                Assert.Equal(0.1, block.BoundingBox!.X0, 10);
            },
            block =>
            {
                Assert.Equal("unknown", block.Type);
                Assert.Equal("future-widget", block.Subtype);
                Assert.Equal("{\"value\":42}", block.Content);
                Assert.Null(block.PageNumber);
            });
        Assert.Contains("\"providerType\":\"mineru-cloud\"", first.ProviderMetadataJson);
        Assert.DoesNotContain("StorageRef", first.ProviderMetadataJson, StringComparison.OrdinalIgnoreCase);

        var markdownArtifact = Assert.Single(
            first.Artifacts,
            artifact => artifact.Type == ArtifactTypes.Markdown);
        await using var markdownContent = await environment.Storage.OpenReadAsync(
            markdownArtifact.StorageRef,
            TestContext.Current.CancellationToken);
        using var reader = new StreamReader(markdownContent, Encoding.UTF8);
        Assert.Equal("# Heading\n\n![figure](images/figure.png)", await reader.ReadToEndAsync(
            TestContext.Current.CancellationToken));
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
                "[{\"type\":\"image\",\"page_id\":0,\"img_path\":\"images/figure.png\"}]"u8.ToArray()),
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
            new ProviderResultNormalizationOptions { TemporaryPath = temporaryPath });

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
                });
        }

        public string RootPath { get; }

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
