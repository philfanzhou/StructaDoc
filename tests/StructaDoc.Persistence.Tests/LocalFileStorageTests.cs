using System.Security.Cryptography;
using StructaDoc.Application.Storage;
using StructaDoc.Adapters.Storage;

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

    [Fact]
    public async Task Same_logical_object_is_idempotent_but_different_content_conflicts()
    {
        using var directory = new TemporaryStorageDirectory();
        var storage = directory.CreateStorage();
        const string storageRef = "parse-runs/abc/provider/result.zip";
        var originalBytes = "same-result"u8.ToArray();

        await using (var original = new MemoryStream(originalBytes))
        {
            await storage.WriteAsync(storageRef, original, maxBytes: 1024);
        }

        await using (var replay = new MemoryStream(originalBytes))
        {
            var storedReplay = await storage.WriteAsync(storageRef, replay, maxBytes: 1024);
            Assert.Equal(originalBytes.Length, storedReplay.SizeBytes);
        }

        await using (var conflict = new MemoryStream("different-result"u8.ToArray()))
        {
            var exception = await Assert.ThrowsAsync<StorageObjectConflictException>(
                () => storage.WriteAsync(storageRef, conflict, maxBytes: 1024));
            Assert.Equal(storageRef, exception.StorageRef);
        }

        await using var persisted = await storage.OpenReadAsync(storageRef);
        using var copy = new MemoryStream();
        await persisted.CopyToAsync(copy);
        Assert.Equal(originalBytes, copy.ToArray());
    }

    [Fact]
    public async Task Delete_prunes_the_directories_the_object_left_behind()
    {
        using var directory = new TemporaryStorageDirectory();
        var storage = directory.CreateStorage();
        const string deletedRef = "parse-runs/abc/assets/image.png";
        const string keptRef = "parse-runs/def/assets/image.png";

        await using (var content = new MemoryStream("image"u8.ToArray()))
        {
            await storage.WriteAsync(deletedRef, content, maxBytes: 1024);
        }

        await using (var content = new MemoryStream("image"u8.ToArray()))
        {
            await storage.WriteAsync(keptRef, content, maxBytes: 1024);
        }

        await storage.DeleteIfExistsAsync(deletedRef);

        Assert.False(Directory.Exists(Path.Combine(directory.Path, "parse-runs", "abc")));
        // A sibling still holding an object stops the walk, and so does the storage root.
        Assert.True(File.Exists(Path.Combine(directory.Path, "parse-runs", "def", "assets", "image.png")));
        Assert.True(Directory.Exists(directory.Path));

        // Deleting the last object leaves the root itself and the staging directory standing, so
        // the storage stays usable without being constructed again.
        await storage.DeleteIfExistsAsync(keptRef);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "parse-runs")));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, ".staging")));

        await using (var content = new MemoryStream("image"u8.ToArray()))
        {
            await storage.WriteAsync(keptRef, content, maxBytes: 1024);
        }

        Assert.True(File.Exists(Path.Combine(directory.Path, "parse-runs", "def", "assets", "image.png")));
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
