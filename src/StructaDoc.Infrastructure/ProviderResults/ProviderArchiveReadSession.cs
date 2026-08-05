using System.IO.Compression;
using System.Text;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

namespace StructaDoc.Infrastructure.ProviderResults;

internal sealed class ProviderArchiveReadSession : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly Stream content;
    private readonly ZipArchive archive;

    private ProviderArchiveReadSession(
        Stream content,
        ZipArchive archive,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        this.content = content;
        this.archive = archive;
        Entries = entries;
    }

    public IReadOnlyDictionary<string, ZipArchiveEntry> Entries { get; }

    public static async Task<ProviderArchiveReadSession> OpenAsync(
        IFileStorage fileStorage,
        StoredProviderArchive storedArchive,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileStorage);
        ArgumentNullException.ThrowIfNull(storedArchive);

        Stream? content = null;
        ZipArchive? archive = null;
        try
        {
            content = await fileStorage.OpenReadAsync(
                storedArchive.StorageRef,
                cancellationToken);
            if (!content.CanSeek)
            {
                content = await CopyToTemporaryAsync(
                    content,
                    storedArchive.SizeBytes,
                    temporaryPath,
                    cancellationToken);
            }
            else if (content.Length != storedArchive.SizeBytes)
            {
                throw Failure(
                    "provider-result-archive-changed",
                    "The stored Provider result size changed after intake.",
                    ProviderFailureCategory.Security);
            }

            content.Seek(0, SeekOrigin.Begin);
            archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var entries = ValidateManifest(archive, storedArchive.Entries);
            return new ProviderArchiveReadSession(content, archive, entries);
        }
        catch
        {
            archive?.Dispose();
            if (content is not null)
            {
                await content.DisposeAsync();
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        archive.Dispose();
        await content.DisposeAsync();
    }

    private static IReadOnlyDictionary<string, ZipArchiveEntry> ValidateManifest(
        ZipArchive archive,
        IReadOnlyList<ProviderArchiveEntry> manifest)
    {
        if (archive.Entries.Count != manifest.Count)
        {
            throw Failure(
                "provider-result-archive-changed",
                "The stored Provider result entries changed after intake.",
                ProviderFailureCategory.Security);
        }

        var expectedEntries = new Dictionary<string, ProviderArchiveEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest)
        {
            if (entry is null
                || !IsSafePath(entry.Path)
                || !expectedEntries.TryAdd(entry.Path, entry))
            {
                throw Failure(
                    "provider-result-archive-changed",
                    "The validated Provider result manifest is inconsistent.",
                    ProviderFailureCategory.Security);
            }
        }

        var actualEntries = new Dictionary<string, ZipArchiveEntry>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Normalize(NormalizationForm.FormC);
            if (!IsSafePath(path)
                || !actualEntries.TryAdd(path, entry)
                || !expectedEntries.TryGetValue(path, out var expected))
            {
                throw Failure(
                    "provider-result-archive-changed",
                    "The stored Provider result entries no longer match the validated manifest.",
                    ProviderFailureCategory.Security);
            }

            var isDirectory = path.EndsWith("/", StringComparison.Ordinal);
            if (expected.IsDirectory != isDirectory
                || expected.SizeBytes != entry.Length
                || expected.CompressedSizeBytes != entry.CompressedLength)
            {
                throw Failure(
                    "provider-result-archive-changed",
                    "The stored Provider result entry metadata changed after intake.",
                    ProviderFailureCategory.Security);
            }
        }

        return actualEntries;
    }

    private static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains("\\", StringComparison.Ordinal)
            || path.Contains(":", StringComparison.Ordinal)
            || path.Any(char.IsControl))
        {
            return false;
        }

        var withoutDirectoryMarker = path.EndsWith("/", StringComparison.Ordinal)
            ? path[..^1]
            : path;
        return withoutDirectoryMarker.Length > 0
            && withoutDirectoryMarker.Split('/').All(segment =>
                segment.Length > 0 && segment is not "." and not "..");
    }

    private static async Task<Stream> CopyToTemporaryAsync(
        Stream source,
        long expectedBytes,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(temporaryPath);
        var path = Path.Combine(temporaryPath, $"{Guid.NewGuid():N}.zip.tmp");
        var temporary = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);

        try
        {
            var buffer = new byte[BufferSize];
            long copiedBytes = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                copiedBytes = checked(copiedBytes + bytesRead);
                if (copiedBytes > expectedBytes)
                {
                    throw Failure(
                        "provider-result-archive-changed",
                        "The stored Provider result size changed after intake.",
                        ProviderFailureCategory.Security);
                }

                await temporary.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            if (copiedBytes != expectedBytes)
            {
                throw Failure(
                    "provider-result-archive-changed",
                    "The stored Provider result size changed after intake.",
                    ProviderFailureCategory.Security);
            }

            temporary.Seek(0, SeekOrigin.Begin);
            return temporary;
        }
        catch
        {
            await temporary.DisposeAsync();
            throw;
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    private static ProviderResultNormalizationException Failure(
        string code,
        string message,
        ProviderFailureCategory category,
        Exception? innerException = null) =>
        new(code, message, category, innerException);
}
