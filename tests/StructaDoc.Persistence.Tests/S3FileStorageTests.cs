using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
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

    private sealed class InMemoryS3Client : AmazonS3Client
    {
        private readonly Dictionary<string, StoredObject> objects = new(StringComparer.Ordinal);

        public InMemoryS3Client()
            : base(
                new AnonymousAWSCredentials(),
                new AmazonS3Config
                {
                    RegionEndpoint = RegionEndpoint.USEast1,
                    ServiceURL = "http://127.0.0.1",
                    ForcePathStyle = true,
                })
        {
        }

        public int PutCount { get; private set; }

        public byte[] Read(string key) => objects[key].Content;

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!objects.TryGetValue(key, out var stored))
            {
                throw new AmazonS3Exception("The object does not exist.")
                {
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            var response = new GetObjectMetadataResponse
            {
                ContentLength = stored.Content.Length,
            };
            response.Metadata["sha256"] = stored.Sha256;
            return Task.FromResult(response);
        }

        public override async Task<PutObjectResponse> PutObjectAsync(
            PutObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            PutCount++;
            Assert.Equal("*", request.IfNoneMatch);
            if (objects.ContainsKey(request.Key))
            {
                throw new AmazonS3Exception("The conditional write lost the race.")
                {
                    StatusCode = HttpStatusCode.PreconditionFailed,
                };
            }

            using var content = new MemoryStream();
            await request.InputStream.CopyToAsync(content, cancellationToken);
            var bytes = content.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(sha256, request.Metadata["sha256"]);
            objects.Add(
                request.Key,
                new StoredObject(bytes, sha256));
            return new PutObjectResponse();
        }

        private sealed record StoredObject(byte[] Content, string Sha256);
    }
}
