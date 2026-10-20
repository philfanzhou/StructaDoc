using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Storage;

namespace StructaDoc.Persistence.Tests;

public sealed class S3FileStorageTests
{
    [Fact]
    public async Task Same_logical_object_is_idempotent_but_different_content_conflicts()
    {
        using var client = new InMemoryS3Client();
        var storage = new S3FileStorage(client, new FileStorageOptions
        {
            Bucket = "structadoc-tests",
            Prefix = "storage-contract",
        });
        const string storageRef = "parse-runs/abc/segments/0000.pdf";
        var originalBytes = "same-segment"u8.ToArray();

        await using (var original = new MemoryStream(originalBytes, writable: false))
        {
            await storage.WriteAsync(
                storageRef,
                original,
                maxBytes: 1024,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var replay = new MemoryStream(originalBytes, writable: false))
        {
            var storedReplay = await storage.WriteAsync(
                storageRef,
                replay,
                maxBytes: 1024,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(originalBytes.Length, storedReplay.SizeBytes);
        }

        await using (var conflict = new MemoryStream("different-segment"u8.ToArray(), writable: false))
        {
            var exception = await Assert.ThrowsAsync<StorageObjectConflictException>(
                () => storage.WriteAsync(
                    storageRef,
                    conflict,
                    maxBytes: 1024,
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(storageRef, exception.StorageRef);
        }

        Assert.Equal(2, client.PutCount);
        Assert.Equal(originalBytes, client.Read("storage-contract/parse-runs/abc/segments/0000.pdf"));
    }
}
