using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Host.ParseRuns;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The ZIP export stages the archive it builds on the configured writable data path rather than
/// the system temporary directory, because a supported container bounds <c>/tmp</c> below the
/// size one permitted result can reach. These tests drive the production service directly with a
/// synthetic read service, so every assertion about where the staged file lives and when it stops
/// existing is deterministic rather than raced against response streaming.
/// </summary>
public sealed class ParseExportServiceTemporaryPathTests
{
    private const string Markdown = """
        # Export

        ![](images/figure-a.png)

        ![](images/figure-b.png)

        ![](images/missing.png)
        """;

    private static readonly byte[] FigureA = [0x89, 0x50, 0x4e, 0x47, 0x01, 0x02, 0x03, 0x04];
    private static readonly byte[] FigureB = [0x89, 0x50, 0x4e, 0x47, 0x05, 0x06, 0x07, 0x08];

    [Fact]
    public void Local_default_is_the_writable_data_tree_and_validates()
    {
        var options = new ParseExportOptions();

        // The local default matches the sibling temporary paths: under ./data, which a
        // self-hosted checkout owns, rather than the system temporary directory.
        Assert.Equal("./data/temp/exports", options.TemporaryPath);
        options.Validate();
    }

    [Fact]
    public async Task Zip_generation_stages_in_the_configured_directory_and_returns_a_complete_result()
    {
        var stagingDirectory = NewStagingDirectory();
        var readService = new SyntheticReadService(Markdown, [Asset("figure-a.png", FigureA), Asset("figure-b.png", FigureB)]);
        var service = CreateService(readService, stagingDirectory);

        var export = await service.CreateAsync(readService.ParseRunId, "zip", ResourceAccessContext.System, TestContext.Current.CancellationToken);

        Assert.NotNull(export);
        // The archive exists on the configured path while the returned content holds it open.
        var staged = Assert.Single(Directory.GetFiles(stagingDirectory));
        Assert.StartsWith("structadoc-export-", Path.GetFileName(staged), StringComparison.Ordinal);

        using var buffer = new MemoryStream();
        await export.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        buffer.Position = 0;

        // The returned archive, length, and hash describe the staged bytes exactly.
        Assert.Equal(buffer.Length, export.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant(),
            export.Sha256);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("document.md", entryNames);
        Assert.Contains("assets/figure-a.png", entryNames);
        Assert.Contains("assets/figure-b.png", entryNames);
        await using var document = archive.GetEntry("document.md")!.Open();
        using var reader = new StreamReader(document);
        var rewritten = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("![](assets/figure-a.png)", rewritten, StringComparison.Ordinal);
        Assert.Contains("![](assets/figure-b.png)", rewritten, StringComparison.Ordinal);
        await using var assetEntry = archive.GetEntry("assets/figure-b.png")!.Open();
        using var assetBuffer = new MemoryStream();
        await assetEntry.CopyToAsync(assetBuffer, TestContext.Current.CancellationToken);
        Assert.Equal(FigureB, assetBuffer.ToArray());

        // Response disposal releases the staged file and its handle; nothing is left to scavenge.
        await export.Content.DisposeAsync();
        Assert.Empty(Directory.GetFiles(stagingDirectory));
    }

    [Fact]
    public async Task Concurrent_exports_stage_distinct_files()
    {
        var stagingDirectory = NewStagingDirectory();
        var readService = new SyntheticReadService(Markdown, [Asset("figure-a.png", FigureA), Asset("figure-b.png", FigureB)]);
        var service = CreateService(readService, stagingDirectory);

        var exports = await Task.WhenAll(
            service.CreateAsync(readService.ParseRunId, "zip", ResourceAccessContext.System, TestContext.Current.CancellationToken),
            service.CreateAsync(readService.ParseRunId, "zip", ResourceAccessContext.System, TestContext.Current.CancellationToken));

        // Two callers both holding an open result never share one staged archive.
        var stagedFiles = Directory.GetFiles(stagingDirectory);
        Assert.Equal(2, stagedFiles.Length);
        Assert.Equal(2, stagedFiles.Distinct(StringComparer.Ordinal).Count());

        foreach (var export in exports)
        {
            Assert.NotNull(export);
            await export.Content.DisposeAsync();
        }

        Assert.Empty(Directory.GetFiles(stagingDirectory));
    }

    [Fact]
    public async Task Storage_read_failure_during_generation_leaves_no_staged_file()
    {
        var stagingDirectory = NewStagingDirectory();
        var readService = new SyntheticReadService(Markdown, [Asset("figure-a.png", FigureA), Asset("figure-b.png", FigureB)])
        {
            FailAssetOpen = true,
        };
        var service = CreateService(readService, stagingDirectory);

        await Assert.ThrowsAsync<ParseResultContentUnavailableException>(
            () => service.CreateAsync(readService.ParseRunId, "zip", ResourceAccessContext.System, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(stagingDirectory));
    }

    [Fact]
    public async Task Zip_write_failure_leaves_no_staged_file()
    {
        var stagingDirectory = NewStagingDirectory();
        var readService = new SyntheticReadService(Markdown, [Asset("figure-a.png", FigureA)]);
        var service = CreateService(readService, stagingDirectory);

        // A staging directory that stops existing between construction and generation is the
        // writable-path failure an operator can produce without filling a disk.
        Directory.Delete(stagingDirectory);

        await Assert.ThrowsAnyAsync<IOException>(
            () => service.CreateAsync(readService.ParseRunId, "zip", ResourceAccessContext.System, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Fact]
    public async Task Hashing_cancellation_releases_the_staged_file_and_its_handle()
    {
        var stagingDirectory = NewStagingDirectory();
        var cancellation = new CancellationTokenSource();
        // The last asset stream's disposal is the moment the finished archive is closed and the
        // content hash begins, so cancelling there puts the cancellation into the hashing phase
        // specifically rather than into archive writing.
        var readService = new SyntheticReadService(
            Markdown,
            [
                Asset("figure-a.png", FigureA),
                new SyntheticAsset("figure-b.png", FigureB, () => new CancelWhenDisposedStream(FigureB, cancellation)),
            ]);
        var service = CreateService(readService, stagingDirectory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateAsync(readService.ParseRunId, "zip", ResourceAccessContext.System, cancellation.Token));

        Assert.Empty(Directory.GetFiles(stagingDirectory));
    }

    private static SyntheticAsset Asset(string name, byte[] bytes) => new(name, bytes);

    private static string NewStagingDirectory() => Path.Combine(
        Path.GetTempPath(),
        "structadoc-export-staging-tests",
        Guid.NewGuid().ToString("N"));

    private static ParseExportService CreateService(IParseResultReadService results, string stagingDirectory) =>
        new(results, new UnusedDocumentReadService(), new ParseExportOptions { TemporaryPath = stagingDirectory });

    /// <summary>A fixed Asset with its metadata, plus control over the stream that serves it.</summary>
    private sealed record SyntheticAsset(string Name, byte[] Bytes, Func<Stream>? OpenContent = null)
    {
        public long SizeBytes => Bytes.Length;

        public string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

        public Stream Open() => OpenContent is null
            ? new MemoryStream(Bytes, writable: false)
            : OpenContent();
    }

    /// <summary>
    /// Serves a fixed Markdown result and fixed Assets, which is everything the ZIP export reads.
    /// Every open returns a fresh stream, because the export disposes what it reads; everything
    /// else the interface offers is outside this fixture's contract and refuses use.
    /// </summary>
    private sealed class SyntheticReadService(string markdown, IReadOnlyList<SyntheticAsset> assets)
        : IParseResultReadService
    {
        public bool FailAssetOpen { get; init; }

        public Guid ParseRunId { get; } = Guid.NewGuid();

        public Task<ParseResultContent?> OpenMarkdownAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ParseRunId, parseRunId);
            var bytes = Encoding.UTF8.GetBytes(markdown);
            return Task.FromResult<ParseResultContent?>(new ParseResultContent(
                new MemoryStream(bytes, writable: false),
                "document.md",
                "text/markdown",
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        public Task<IReadOnlyList<ParseExportAssetRecord>?> ListAssetsForExportAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ParseRunId, parseRunId);
            IReadOnlyList<ParseExportAssetRecord> records =
                [.. assets.Select(asset => new ParseExportAssetRecord(
                    ParseRunId,
                    Guid.NewGuid(),
                    asset.Name,
                    "image/png",
                    asset.SizeBytes,
                    asset.Sha256,
                    null,
                    null,
                    $"parse-runs/{parseRunId:N}/assets/{asset.Name}"))];
            return Task.FromResult<IReadOnlyList<ParseExportAssetRecord>?>(records);
        }

        public Task<ParseResultContent?> OpenExportAssetAsync(Guid parseRunId, ParseExportAssetRecord asset, CancellationToken cancellationToken = default)
        {
            if (FailAssetOpen)
            {
                throw new ParseResultContentUnavailableException(
                    parseRunId,
                    new FileNotFoundException());
            }

            var content = assets.Single(candidate => candidate.Name == asset.Name);
            return Task.FromResult<ParseResultContent?>(new ParseResultContent(
                content.Open(),
                content.Name,
                "image/png",
                content.SizeBytes,
                content.Sha256));
        }

        public Task<ParseRunRecord?> GetAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default) => Refuse<ParseRunRecord?>();

        public Task<IReadOnlyList<ParseRunRecord>> ListForDocumentAsync(Guid documentId, ResourceAccessContext access, CancellationToken cancellationToken = default) => Refuse<IReadOnlyList<ParseRunRecord>>();

        public Task<IReadOnlyList<ParsePageRecord>?> ListPagesAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default) => Refuse<IReadOnlyList<ParsePageRecord>?>();

        public Task<ParseBlockPage?> ListBlocksAsync(Guid parseRunId, ResourceAccessContext access, int limit, int? afterSequence = null, int? pageNumber = null, CancellationToken cancellationToken = default) => Refuse<ParseBlockPage?>();

        public Task<IReadOnlyList<ParseAssetRecord>?> ListAssetsAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default) => Refuse<IReadOnlyList<ParseAssetRecord>?>();

        public Task<IReadOnlyList<ParseArtifactRecord>?> ListArtifactsAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default) => Refuse<IReadOnlyList<ParseArtifactRecord>?>();

        public Task<ParseResultContent?> OpenAssetAsync(Guid parseRunId, Guid assetId, ResourceAccessContext access, CancellationToken cancellationToken = default) => Refuse<ParseResultContent?>();

        public Task<ParseResultContent?> OpenArtifactAsync(Guid parseRunId, Guid artifactId, ResourceAccessContext access, CancellationToken cancellationToken = default) => Refuse<ParseResultContent?>();

        private static Task<T> Refuse<T>() => throw new NotSupportedException(
            "This synthetic read service serves ZIP exports only.");
    }

    private sealed class UnusedDocumentReadService : IDocumentReadService
    {
        public Task<DocumentPage> ListAsync(int limit, DocumentCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentContent?> OpenContentAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentPage> ListAccessibleAsync(int limit, ResourceAccessContext access, DocumentCursor? cursor = null, string? fileName = null, string? parseStatus = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentRecord?> GetAccessibleAsync(Guid id, ResourceAccessContext access, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentContent?> OpenAccessibleContentAsync(Guid id, ResourceAccessContext access, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// A read-only stream over fixed bytes that cancels a source when it is disposed, so a test
    /// can place a cancellation at the exact boundary between a completed archive write and the
    /// content hash of the staged file.
    /// </summary>
    private sealed class CancelWhenDisposedStream(byte[] bytes, CancellationTokenSource cancellation) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);
        private bool disposed;

        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read == 0) cancellation.Cancel();
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                inner.Dispose();
                cancellation.Cancel();
                disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
