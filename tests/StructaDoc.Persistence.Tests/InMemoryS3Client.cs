using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace StructaDoc.Persistence.Tests;

/// <summary>
/// An in-memory S3 stub used by storage and deletion-inventory tests. It serves the same
/// observable contract as <see cref="Adapters.Storage.S3FileStorage"/> expects — conditional
/// writes, metadata-based idempotency, and object reads — without network access or listing.
/// </summary>
internal sealed class InMemoryS3Client : AmazonS3Client
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

    /// <summary>
    /// When set, object reads fail with a server error so tests can exercise storage-outage
    /// handling that must stay distinguishable from a truly missing object.
    /// </summary>
    public bool FailReads { get; set; }

    public int PutCount { get; private set; }

    public byte[] Read(string key) => objects[key].Content;

    public bool Contains(string key) => objects.ContainsKey(key);

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

    public override Task<GetObjectResponse> GetObjectAsync(
        string bucketName,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailReads)
        {
            throw new AmazonS3Exception("The storage service is unavailable.")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
            };
        }

        if (!objects.TryGetValue(key, out var stored))
        {
            throw new AmazonS3Exception("The object does not exist.")
            {
                StatusCode = HttpStatusCode.NotFound,
            };
        }

        var response = new GetObjectResponse
        {
            ResponseStream = new MemoryStream(stored.Content, writable: false),
        };
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

    public override Task<DeleteObjectResponse> DeleteObjectAsync(
        string bucketName,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        objects.Remove(key);
        return Task.FromResult(new DeleteObjectResponse());
    }

    private sealed record StoredObject(byte[] Content, string Sha256);
}
