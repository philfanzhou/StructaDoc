using System.IO.Compression;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Adapters.Storage;

namespace StructaDoc.Persistence.Tests;

public sealed class ProviderResultIntakeTests
{
    [Fact]
    public async Task Valid_archive_is_streamed_validated_and_idempotently_stored()
    {
        using var environment = new IntakeTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var archiveBytes = CreateArchive(
            ("content_list.json", "[{\"type\":\"text\"}]"u8.ToArray(), null),
            ("images/page-1.png", new byte[] { 1, 2, 3, 4 }, null));

        var first = await environment.StoreAsync(
            parseRunId,
            archiveBytes,
            "application/octet-stream; charset=binary",
            "mineru-result.zip");
        var replay = await environment.StoreAsync(
            parseRunId,
            archiveBytes,
            "application/zip",
            "mineru-result.zip");

        Assert.Equal("application/zip", first.MediaType);
        Assert.Equal("provider-result.zip", first.Name);
        Assert.Equal($"parse-runs/{parseRunId:N}/provider/result.zip", first.StorageRef);
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(21, first.ExpandedSizeBytes);
        Assert.Equal(first.SizeBytes, replay.SizeBytes);
        Assert.Equal(first.Sha256, replay.Sha256);
        Assert.Equal(first.StorageRef, replay.StorageRef);
        Assert.Single(Directory.GetFiles(environment.StoragePath, "*", SearchOption.AllDirectories));

        var recovered = await environment.Intake.TryLoadArchiveAsync(
            parseRunId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(recovered);
        Assert.Equal(first.Sha256, recovered.Sha256);
        Assert.Equal(first.Entries.ToArray(), recovered.Entries.ToArray());
    }

    [Fact]
    public async Task Missing_stored_archive_returns_null_for_recovery()
    {
        using var environment = new IntakeTestEnvironment();

        var recovered = await environment.Intake.TryLoadArchiveAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.Null(recovered);
    }

    [Fact]
    public async Task Recovery_revalidates_and_removes_an_invalid_stored_archive()
    {
        using var environment = new IntakeTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var storageRef = $"parse-runs/{parseRunId:N}/provider/result.zip";
        await using (var invalid = new MemoryStream("not-a-zip"u8.ToArray()))
        {
            await environment.Storage.WriteAsync(
                storageRef,
                invalid,
                maxBytes: 1024,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
            environment.Intake.TryLoadArchiveAsync(
                parseRunId,
                TestContext.Current.CancellationToken));

        Assert.Equal("provider-result-not-zip", exception.ErrorCode);
        Assert.Empty(Directory.GetFiles(environment.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside.txt", "provider-result-archive-unsafe-path")]
    [InlineData("/absolute.txt", "provider-result-archive-unsafe-path")]
    [InlineData("folder\\windows.txt", "provider-result-archive-unsafe-path")]
    [InlineData("folder//empty.txt", "provider-result-archive-unsafe-path")]
    public async Task Unsafe_archive_paths_are_rejected_and_removed(
        string entryPath,
        string expectedCode)
    {
        using var environment = new IntakeTestEnvironment();
        var archiveBytes = CreateArchive((entryPath, "unsafe"u8.ToArray(), null));

        var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
            environment.StoreAsync(Guid.NewGuid(), archiveBytes));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Security, exception.Category);
        Assert.DoesNotContain(entryPath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(environment.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Duplicate_portable_paths_are_rejected()
    {
        using var environment = new IntakeTestEnvironment();
        var archiveBytes = CreateArchive(
            ("Images/Page.png", new byte[] { 1 }, null),
            ("images/page.png", new byte[] { 2 }, null));

        var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
            environment.StoreAsync(Guid.NewGuid(), archiveBytes));

        Assert.Equal("provider-result-archive-duplicate-path", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Security, exception.Category);
        Assert.Empty(Directory.GetFiles(environment.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Link_entries_are_rejected()
    {
        using var environment = new IntakeTestEnvironment();
        var linkAttributes = unchecked((int)0xA1FF0000);
        var archiveBytes = CreateArchive(("link", "target"u8.ToArray(), linkAttributes));

        var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
            environment.StoreAsync(Guid.NewGuid(), archiveBytes));

        Assert.Equal("provider-result-archive-special-entry", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Security, exception.Category);
    }

    [Fact]
    public async Task Expanded_size_and_compression_ratio_are_enforced_while_reading()
    {
        var archiveBytes = CreateArchive(
            ("one.txt", new byte[24], null),
            ("two.txt", new byte[24], null));

        using (var environment = new IntakeTestEnvironment(options =>
        {
            options.MaxEntryBytes = 32;
            options.MaxExpandedBytes = 32;
            options.MaxCompressionRatio = 1000;
        }))
        {
            var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
                environment.StoreAsync(Guid.NewGuid(), archiveBytes));
            Assert.Equal("provider-result-archive-expanded-limit", exception.ErrorCode);
        }

        using (var environment = new IntakeTestEnvironment(options =>
        {
            options.MaxCompressionRatio = 2;
        }))
        {
            var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
                environment.StoreAsync(Guid.NewGuid(), archiveBytes));
            Assert.Equal("provider-result-archive-compression-ratio", exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Entry_count_and_archive_signature_are_enforced()
    {
        using (var environment = new IntakeTestEnvironment(options => options.MaxEntryCount = 1))
        {
            var archiveBytes = CreateArchive(
                ("one.txt", new byte[] { 1 }, null),
                ("two.txt", new byte[] { 2 }, null));
            var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
                environment.StoreAsync(Guid.NewGuid(), archiveBytes));
            Assert.Equal("provider-result-archive-entry-limit", exception.ErrorCode);
        }

        using (var environment = new IntakeTestEnvironment())
        {
            var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
                environment.StoreAsync(Guid.NewGuid(), "not-a-zip"u8.ToArray()));
            Assert.Equal("provider-result-not-zip", exception.ErrorCode);

            exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
                environment.StoreAsync(
                    Guid.NewGuid(),
                    new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 2, 3 }));
            Assert.Equal("provider-result-archive-invalid", exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Compressed_size_limit_is_enforced_without_leaving_an_object()
    {
        using var environment = new IntakeTestEnvironment(options => options.MaxArchiveBytes = 32);
        var archiveBytes = CreateArchive(("result.txt", new byte[128], null));

        var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
            environment.StoreAsync(Guid.NewGuid(), archiveBytes));

        Assert.Equal("provider-result-too-large", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
        Assert.Empty(Directory.GetFiles(environment.StoragePath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Central_directory_and_raw_path_metadata_are_preflighted()
    {
        using (var environment = new IntakeTestEnvironment(
            options => options.MaxCentralDirectoryBytes = 46))
        {
            var archiveBytes = CreateArchive(("result.txt", new byte[] { 1 }, null));
            var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
                environment.StoreAsync(Guid.NewGuid(), archiveBytes));
            Assert.Equal("provider-result-archive-directory-size-limit", exception.ErrorCode);
        }

        using (var environment = new IntakeTestEnvironment(options =>
        {
            options.MaxEntryPathBytes = 64;
        }))
        {
            var archiveBytes = CreateArchive(($"{new string('a', 65)}.txt", new byte[] { 1 }, null));
            var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
                environment.StoreAsync(Guid.NewGuid(), archiveBytes));
            Assert.Equal("provider-result-archive-unsafe-path", exception.ErrorCode);
        }
    }

    [Fact]
    public async Task Non_seekable_storage_reads_use_a_bounded_temporary_file()
    {
        using var environment = new IntakeTestEnvironment();
        var archiveBytes = CreateArchive(("result.txt", "content"u8.ToArray(), null));
        var intake = new StoredProviderResultIntake(
            new NonSeekableReadStorage(environment.Storage),
            environment.Options);
        await using var result = new ProviderResultContent(
            new MemoryStream(archiveBytes, writable: false),
            "application/zip");

        var stored = await intake.StoreArchiveAsync(
            Guid.NewGuid(),
            result,
            TestContext.Current.CancellationToken);

        Assert.Single(stored.Entries);
        Assert.Equal("result.txt", stored.Entries[0].Path);
        Assert.Empty(Directory.GetFiles(environment.TemporaryPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Different_replay_content_conflicts_without_deleting_the_original()
    {
        using var environment = new IntakeTestEnvironment();
        var parseRunId = Guid.NewGuid();
        var originalBytes = CreateArchive(("result.txt", "first"u8.ToArray(), null));
        var differentBytes = CreateArchive(("result.txt", "second"u8.ToArray(), null));
        var first = await environment.StoreAsync(parseRunId, originalBytes);

        var exception = await Assert.ThrowsAsync<ProviderResultIntakeException>(() =>
            environment.StoreAsync(parseRunId, differentBytes));

        Assert.Equal("provider-result-storage-conflict", exception.ErrorCode);
        await using var persisted = await environment.Storage.OpenReadAsync(
            first.StorageRef,
            TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await persisted.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(originalBytes, copy.ToArray());
    }

    private static byte[] CreateArchive(
        params (string Path, byte[] Content, int? ExternalAttributes)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.SmallestSize);
                if (item.ExternalAttributes.HasValue)
                {
                    entry.ExternalAttributes = item.ExternalAttributes.Value;
                }

                using var content = entry.Open();
                content.Write(item.Content);
            }
        }

        return output.ToArray();
    }

    private sealed class IntakeTestEnvironment : IDisposable
    {
        public IntakeTestEnvironment(Action<MutableOptions>? configure = null)
        {
            StoragePath = Path.Combine(
                Path.GetTempPath(),
                "structadoc-result-intake-tests",
                Guid.NewGuid().ToString("N"));
            TemporaryPath = Path.Combine(StoragePath, "temp");
            Directory.CreateDirectory(StoragePath);
            Storage = new LocalFileStorage(new FileStorageOptions { RootPath = StoragePath });

            var mutable = new MutableOptions();
            configure?.Invoke(mutable);
            Options = new ProviderResultIntakeOptions
            {
                MaxArchiveBytes = mutable.MaxArchiveBytes,
                MaxEntryCount = mutable.MaxEntryCount,
                MaxEntryBytes = mutable.MaxEntryBytes,
                MaxExpandedBytes = mutable.MaxExpandedBytes,
                MaxCompressionRatio = mutable.MaxCompressionRatio,
                MaxEntryPathBytes = mutable.MaxEntryPathBytes,
                MaxCentralDirectoryBytes = mutable.MaxCentralDirectoryBytes,
                TemporaryPath = TemporaryPath,
            };
            Intake = new StoredProviderResultIntake(Storage, Options);
        }

        public string StoragePath { get; }

        public string TemporaryPath { get; }

        public LocalFileStorage Storage { get; }

        public ProviderResultIntakeOptions Options { get; }

        public StoredProviderResultIntake Intake { get; }

        public async Task<StoredProviderArchive> StoreAsync(
            Guid parseRunId,
            byte[] content,
            string mediaType = "application/zip",
            string? fileName = null)
        {
            await using var result = new ProviderResultContent(
                new MemoryStream(content, writable: false),
                mediaType,
                fileName);
            return await Intake.StoreArchiveAsync(parseRunId, result);
        }

        public void Dispose()
        {
            Directory.Delete(StoragePath, recursive: true);
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

    private sealed class MutableOptions
    {
        public long MaxArchiveBytes { get; set; } = 1024 * 1024;

        public int MaxEntryCount { get; set; } = 100;

        public long MaxEntryBytes { get; set; } = 1024 * 1024;

        public long MaxExpandedBytes { get; set; } = 2 * 1024 * 1024;

        public double MaxCompressionRatio { get; set; } = 200;

        public int MaxEntryPathBytes { get; set; } = 1024;

        public long MaxCentralDirectoryBytes { get; set; } = 64 * 1024;
    }
}
