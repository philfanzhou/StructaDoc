using System.Security.Cryptography;
using StructaDoc.Application.Storage;
using StructaDoc.Infrastructure.Storage;

namespace StructaDoc.Persistence.Tests;

public sealed class LocalFileStorageTests
{
    [Fact]
    public async Task Write_streams_content_and_returns_verified_metadata()
    {
        using var directory = new TemporaryStorageDirectory();
        var storage = directory.CreateStorage();
        var contentBytes = "structadoc"u8.ToArray();
        await using var content = new MemoryStream(contentBytes);

        var stored = await storage.WriteAsync(
            "documents/abc/original",
            content,
            maxBytes: 1024);

        Assert.Equal(contentBytes.Length, stored.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant(),
            stored.Sha256);

        await using var storedContent = await storage.OpenReadAsync(stored.StorageRef);
        using var copy = new MemoryStream();
        await storedContent.CopyToAsync(copy);
        Assert.Equal(contentBytes, copy.ToArray());
    }

    [Fact]
    public async Task Oversized_content_does_not_leave_a_target_or_staging_file()
    {
        using var directory = new TemporaryStorageDirectory();
        var storage = directory.CreateStorage();
        await using var content = new MemoryStream(new byte[11]);

        await Assert.ThrowsAsync<FileSizeLimitExceededException>(
            () => storage.WriteAsync("documents/abc/original", content, maxBytes: 10));

        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("documents/../../outside")]
    [InlineData("C:/outside")]
    [InlineData("documents\\outside")]
    public async Task Storage_reference_cannot_escape_the_root(string storageRef)
    {
        using var directory = new TemporaryStorageDirectory();
        var storage = directory.CreateStorage();
        await using var content = new MemoryStream("content"u8.ToArray());

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.WriteAsync(storageRef, content, maxBytes: 1024));
    }

    private sealed class TemporaryStorageDirectory : IDisposable
    {
        public TemporaryStorageDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "structadoc-storage-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public LocalFileStorage CreateStorage()
        {
            return new LocalFileStorage(new FileStorageOptions
            {
                RootPath = Path,
            });
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
