using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using StructaDoc.Application.Storage;

namespace StructaDoc.Platform.Storage;

public sealed class S3FileStorage(IAmazonS3 client, FileStorageOptions options) : IFileStorage
{
    private const int BufferSize = 64 * 1024;

    public async Task<StoredFile> WriteAsync(string storageRef, Stream content, long maxBytes, CancellationToken cancellationToken = default)
    {
        ValidateRef(storageRef);
        var temp = Path.Combine(Path.GetTempPath(), $"structadoc-s3-{Guid.NewGuid():N}.tmp");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken); if (read == 0) break;
                    size = checked(size + read); if (size > maxBytes) throw new FileSizeLimitExceededException(maxBytes);
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (size == 0) throw new InvalidDataException("The uploaded file is empty.");
            var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var key = Key(storageRef);
            if (await MatchesExistingAsync(key, size, sha, cancellationToken)) return new StoredFile(storageRef, size, sha);
            await using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var request = new PutObjectRequest { BucketName = options.Bucket!, Key = key, InputStream = input, AutoCloseStream = false, IfNoneMatch = "*" };
            request.Metadata["sha256"] = sha;
            try { await client.PutObjectAsync(request, cancellationToken); }
            catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                if (!await MatchesExistingAsync(key, size, sha, cancellationToken)) throw new StorageObjectConflictException(storageRef);
            }
            return new StoredFile(storageRef, size, sha);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<Stream> OpenReadAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        ValidateRef(storageRef);
        var response = await client.GetObjectAsync(options.Bucket!, Key(storageRef), cancellationToken);
        return response.ResponseStream;
    }

    public async Task DeleteIfExistsAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        ValidateRef(storageRef);
        await client.DeleteObjectAsync(options.Bucket!, Key(storageRef), cancellationToken);
    }

    private async Task<bool> MatchesExistingAsync(string key, long size, string sha, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectMetadataAsync(options.Bucket!, key, cancellationToken);
            return response.ContentLength == size && string.Equals(response.Metadata["x-amz-meta-sha256"], sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    private string Key(string storageRef) => string.IsNullOrWhiteSpace(options.Prefix) ? storageRef : $"{options.Prefix.Trim('/')}/{storageRef}";
    private static void ValidateRef(string storageRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRef);
        if (storageRef.StartsWith('/') || storageRef.Contains('\\') || storageRef.Split('/').Any(segment => segment is "" or "." or "..")) throw new ArgumentException("Storage reference must be a safe relative POSIX path.", nameof(storageRef));
    }
}
