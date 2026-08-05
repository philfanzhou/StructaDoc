using System.Buffers;
using System.Security.Cryptography;
using StructaDoc.Application.Storage;

namespace StructaDoc.Infrastructure.Storage;

public sealed class LocalFileStorage
    : IFileStorage
{
    private const int BufferSize = 64 * 1024;
    private readonly string rootPath;
    private readonly string stagingPath;

    public LocalFileStorage(FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        rootPath = Path.GetFullPath(options.RootPath);
        stagingPath = Path.Combine(rootPath, ".staging");
        Directory.CreateDirectory(stagingPath);
    }

    public async Task<StoredFile> WriteAsync(
        string storageRef,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var destinationPath = ResolveStorageRef(storageRef);
        var temporaryPath = Path.Combine(stagingPath, $"{Guid.NewGuid():N}.tmp");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long sizeBytes = 0;

            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var bytesRead = await content.ReadAsync(buffer, cancellationToken);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    sizeBytes = checked(sizeBytes + bytesRead);

                    if (sizeBytes > maxBytes)
                    {
                        throw new FileSizeLimitExceededException(maxBytes);
                    }

                    hash.AppendData(buffer, 0, bytesRead);
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            if (sizeBytes == 0)
            {
                throw new InvalidDataException("The uploaded file is empty.");
            }

            var storedFile = new StoredFile(
                storageRef,
                sizeBytes,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            try
            {
                File.Move(temporaryPath, destinationPath);
                return storedFile;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                if (await MatchesExistingAsync(destinationPath, storedFile, cancellationToken))
                {
                    return storedFile;
                }

                throw new StorageObjectConflictException(storageRef);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(
        string storageRef,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            ResolveStorageRef(storageRef),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteIfExistsAsync(
        string storageRef,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveStorageRef(storageRef);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolveStorageRef(string storageRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRef);

        if (Path.IsPathRooted(storageRef)
            || storageRef.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage references must be relative POSIX paths.", nameof(storageRef));
        }

        var segments = storageRef.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new ArgumentException("Storage reference contains an invalid path segment.", nameof(storageRef));
        }

        var resolvedPath = Path.GetFullPath(Path.Combine([rootPath, .. segments]));
        var relativePath = Path.GetRelativePath(rootPath, resolvedPath);

        if (relativePath.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Storage reference escapes the configured root.", nameof(storageRef));
        }

        return resolvedPath;
    }

    private static async Task<bool> MatchesExistingAsync(
        string path,
        StoredFile candidate,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length != candidate.SizeBytes)
        {
            return false;
        }

        await using var existing = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(existing, cancellationToken);
        return string.Equals(
            Convert.ToHexString(hash),
            candidate.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }
}
