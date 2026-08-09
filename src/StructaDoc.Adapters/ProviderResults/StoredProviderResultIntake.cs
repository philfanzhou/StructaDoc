using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

namespace StructaDoc.Adapters.ProviderResults;

public sealed class StoredProviderResultIntake(
    IFileStorage fileStorage,
    ProviderResultIntakeOptions options) : IProviderResultIntake
{
    private const int BufferSize = 64 * 1024;
    private const string CanonicalArchiveMediaType = "application/zip";
    private static readonly HashSet<string> AcceptedArchiveMediaTypes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        CanonicalArchiveMediaType,
        "application/x-zip-compressed",
        "application/octet-stream",
    };

    public async Task<StoredProviderArchive> StoreArchiveAsync(
        Guid parseRunId,
        ProviderResultContent result,
        CancellationToken cancellationToken = default)
    {
        if (parseRunId == Guid.Empty)
        {
            throw new ArgumentException("A Parse Run ID is required.", nameof(parseRunId));
        }

        ArgumentNullException.ThrowIfNull(result);
        ValidateArchiveMediaType(result.MediaType);
        options.Validate();

        var storageRef = $"parse-runs/{parseRunId:N}/provider/result.zip";
        StoredFile storedFile;

        try
        {
            storedFile = await fileStorage.WriteAsync(
                storageRef,
                result.Content,
                options.MaxArchiveBytes,
                cancellationToken);
        }
        catch (FileSizeLimitExceededException exception)
        {
            throw IntakeFailure(
                "provider-result-too-large",
                "The Provider result archive exceeds the configured compressed size limit.",
                ProviderFailureCategory.Permanent,
                exception);
        }
        catch (StorageObjectConflictException exception)
        {
            throw IntakeFailure(
                "provider-result-storage-conflict",
                "The Provider returned different content for an existing Parse Run result.",
                ProviderFailureCategory.Permanent,
                exception);
        }

        try
        {
            await using var storedContent = await fileStorage.OpenReadAsync(
                storedFile.StorageRef,
                cancellationToken);
            var validation = await ValidateArchiveAsync(
                storedContent,
                storedFile.SizeBytes,
                cancellationToken);

            return new StoredProviderArchive(
                "provider-result.zip",
                CanonicalArchiveMediaType,
                storedFile.StorageRef,
                storedFile.SizeBytes,
                storedFile.Sha256,
                validation.Entries,
                validation.ExpandedSizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderResultIntakeException)
        {
            await fileStorage.DeleteIfExistsAsync(storedFile.StorageRef, CancellationToken.None);
            throw;
        }
        catch (InvalidDataException exception)
        {
            await fileStorage.DeleteIfExistsAsync(storedFile.StorageRef, CancellationToken.None);
            throw IntakeFailure(
                "provider-result-archive-invalid",
                "The Provider result is not a readable ZIP archive.",
                ProviderFailureCategory.Permanent,
                exception);
        }
    }

    public async Task<StoredProviderArchive?> TryLoadArchiveAsync(
        Guid parseRunId,
        CancellationToken cancellationToken = default)
    {
        if (parseRunId == Guid.Empty)
        {
            throw new ArgumentException("A Parse Run ID is required.", nameof(parseRunId));
        }

        options.Validate();
        var storageRef = $"parse-runs/{parseRunId:N}/provider/result.zip";
        StoredFile storedFile;

        try
        {
            await using var metadataContent = await fileStorage.OpenReadAsync(
                storageRef,
                cancellationToken);
            storedFile = await ComputeStoredFileAsync(
                storageRef,
                metadataContent,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (ProviderResultIntakeException)
        {
            await fileStorage.DeleteIfExistsAsync(storageRef, CancellationToken.None);
            throw;
        }

        try
        {
            await using var storedContent = await fileStorage.OpenReadAsync(
                storageRef,
                cancellationToken);
            var validation = await ValidateArchiveAsync(
                storedContent,
                storedFile.SizeBytes,
                cancellationToken);
            return new StoredProviderArchive(
                "provider-result.zip",
                CanonicalArchiveMediaType,
                storedFile.StorageRef,
                storedFile.SizeBytes,
                storedFile.Sha256,
                validation.Entries,
                validation.ExpandedSizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (ProviderResultIntakeException)
        {
            await fileStorage.DeleteIfExistsAsync(storageRef, CancellationToken.None);
            throw;
        }
        catch (InvalidDataException exception)
        {
            await fileStorage.DeleteIfExistsAsync(storageRef, CancellationToken.None);
            throw IntakeFailure(
                "provider-result-archive-invalid",
                "The stored Provider result is not a readable ZIP archive.",
                ProviderFailureCategory.Permanent,
                exception);
        }
    }

    private async Task<ArchiveValidation> ValidateArchiveAsync(
        Stream content,
        long expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        if (content.CanSeek)
        {
            content.Seek(0, SeekOrigin.Begin);
            return await ValidateSeekableArchiveAsync(content, cancellationToken);
        }

        Directory.CreateDirectory(options.TemporaryPath);
        var temporaryPath = Path.Combine(
            options.TemporaryPath,
            $"{Guid.NewGuid():N}.zip.tmp");

        await using var temporary = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        await CopyExactAsync(content, temporary, expectedSizeBytes, cancellationToken);
        temporary.Seek(0, SeekOrigin.Begin);
        return await ValidateSeekableArchiveAsync(temporary, cancellationToken);
    }

    private async Task<ArchiveValidation> ValidateSeekableArchiveAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        if (!await HasZipSignatureAsync(content, cancellationToken))
        {
            throw IntakeFailure(
                "provider-result-not-zip",
                "The Provider result content does not have a ZIP signature.",
                ProviderFailureCategory.Permanent);
        }

        content.Seek(0, SeekOrigin.Begin);
        var preflightEntryCount = await PreflightCentralDirectoryAsync(content, cancellationToken);
        content.Seek(0, SeekOrigin.Begin);
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count == 0)
        {
            throw IntakeFailure(
                "provider-result-archive-empty",
                "The Provider result archive contains no entries.",
                ProviderFailureCategory.Permanent);
        }

        if (archive.Entries.Count > options.MaxEntryCount)
        {
            throw IntakeFailure(
                "provider-result-archive-entry-limit",
                "The Provider result archive exceeds the configured entry count limit.",
                ProviderFailureCategory.Security);
        }

        if (archive.Entries.Count != preflightEntryCount)
        {
            throw IntakeFailure(
                "provider-result-archive-invalid",
                "The Provider result ZIP central directory is inconsistent.",
                ProviderFailureCategory.Permanent);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ProviderArchiveEntry>(archive.Entries.Count);
        long expandedSizeBytes = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                var path = ValidateEntryPath(entry.FullName, isDirectory);

                if (!paths.Add(path))
                {
                    throw IntakeFailure(
                        "provider-result-archive-duplicate-path",
                        "The Provider result archive contains duplicate entry paths.",
                        ProviderFailureCategory.Security);
                }

                ValidateEntryType(entry, isDirectory);

                if (isDirectory)
                {
                    if (entry.Length != 0)
                    {
                        throw IntakeFailure(
                            "provider-result-archive-invalid-directory",
                            "A Provider result archive directory entry contains file data.",
                            ProviderFailureCategory.Security);
                    }

                    entries.Add(new ProviderArchiveEntry(path, true, 0, entry.CompressedLength));
                    continue;
                }

                if (entry.Length > options.MaxEntryBytes)
                {
                    throw IntakeFailure(
                        "provider-result-archive-entry-size-limit",
                        "A Provider result archive entry exceeds the configured size limit.",
                        ProviderFailureCategory.Security);
                }

                await using var entryContent = entry.Open();
                long actualEntrySize = 0;
                while (true)
                {
                    var bytesRead = await entryContent.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    actualEntrySize = AddWithLimit(
                        actualEntrySize,
                        bytesRead,
                        options.MaxEntryBytes,
                        "provider-result-archive-entry-size-limit",
                        "A Provider result archive entry exceeds the configured size limit.");
                    expandedSizeBytes = AddWithLimit(
                        expandedSizeBytes,
                        bytesRead,
                        options.MaxExpandedBytes,
                        "provider-result-archive-expanded-limit",
                        "The Provider result archive exceeds the configured expanded size limit.");
                }

                if (actualEntrySize != entry.Length)
                {
                    throw IntakeFailure(
                        "provider-result-archive-size-mismatch",
                        "A Provider result archive entry does not match its declared size.",
                        ProviderFailureCategory.Security);
                }

                ValidateCompressionRatio(actualEntrySize, entry.CompressedLength);
                entries.Add(new ProviderArchiveEntry(
                    path,
                    false,
                    actualEntrySize,
                    entry.CompressedLength));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ArchiveValidation(entries, expandedSizeBytes);
    }

    private async Task<int> PreflightCentralDirectoryAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        const uint endOfCentralDirectorySignature = 0x06054B50;
        const uint centralDirectoryHeaderSignature = 0x02014B50;
        const int endOfCentralDirectorySize = 22;
        const int maxCommentSize = ushort.MaxValue;
        const int centralDirectoryHeaderSize = 46;

        if (content.Length < endOfCentralDirectorySize)
        {
            throw new InvalidDataException("ZIP end-of-central-directory record is missing.");
        }

        var tailLength = (int)Math.Min(
            content.Length,
            endOfCentralDirectorySize + maxCommentSize);
        var tail = new byte[tailLength];
        content.Seek(-tailLength, SeekOrigin.End);
        await content.ReadExactlyAsync(tail, cancellationToken);

        var endRecordOffset = -1;
        for (var index = tail.Length - endOfCentralDirectorySize; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4))
                    != endOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
                tail.AsSpan(index + 20, 2));
            if (index + endOfCentralDirectorySize + commentLength == tail.Length)
            {
                endRecordOffset = index;
                break;
            }
        }

        if (endRecordOffset < 0)
        {
            throw new InvalidDataException("ZIP end-of-central-directory record is invalid.");
        }

        var endRecord = tail.AsSpan(endRecordOffset, endOfCentralDirectorySize);
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[4..6]);
        var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[6..8]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[8..10]);
        var declaredEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[10..12]);
        var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[12..16]);
        var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..20]);

        if (diskNumber != 0 || centralDirectoryDisk != 0 || entriesOnDisk != declaredEntryCount)
        {
            throw IntakeFailure(
                "provider-result-archive-multidisk-unsupported",
                "Multi-disk Provider result archives are not supported.",
                ProviderFailureCategory.Security);
        }

        if (declaredEntryCount == ushort.MaxValue
            || centralDirectorySize == uint.MaxValue
            || centralDirectoryOffset == uint.MaxValue)
        {
            throw IntakeFailure(
                "provider-result-archive-zip64-unsupported",
                "ZIP64 Provider result archives are not supported by the current intake limits.",
                ProviderFailureCategory.Security);
        }

        if (declaredEntryCount > options.MaxEntryCount)
        {
            throw EntryCountLimit();
        }

        if (centralDirectorySize > options.MaxCentralDirectoryBytes)
        {
            throw IntakeFailure(
                "provider-result-archive-directory-size-limit",
                "The Provider result ZIP central directory exceeds its configured size limit.",
                ProviderFailureCategory.Security);
        }

        var absoluteEndRecordOffset = content.Length - tailLength + endRecordOffset;
        var centralDirectoryEnd = (long)centralDirectoryOffset + centralDirectorySize;
        if (centralDirectoryEnd > absoluteEndRecordOffset
            || (declaredEntryCount > 0
                && centralDirectorySize < declaredEntryCount * centralDirectoryHeaderSize))
        {
            throw new InvalidDataException("ZIP central directory bounds are invalid.");
        }

        content.Seek(centralDirectoryOffset, SeekOrigin.Begin);
        var header = new byte[centralDirectoryHeaderSize];
        long consumedBytes = 0;
        var actualEntryCount = 0;

        while (consumedBytes < centralDirectorySize)
        {
            if (centralDirectorySize - consumedBytes < centralDirectoryHeaderSize)
            {
                throw new InvalidDataException("ZIP central directory header is truncated.");
            }

            await content.ReadExactlyAsync(header, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4))
                != centralDirectoryHeaderSignature)
            {
                throw new InvalidDataException("ZIP central directory entry signature is invalid.");
            }

            var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
            var extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            if (fileNameLength > options.MaxEntryPathBytes)
            {
                throw UnsafePath();
            }

            var variableLength = (long)fileNameLength + extraFieldLength + commentLength;
            var recordLength = centralDirectoryHeaderSize + variableLength;
            if (recordLength > centralDirectorySize - consumedBytes)
            {
                throw new InvalidDataException("ZIP central directory entry exceeds its bounds.");
            }

            actualEntryCount++;
            if (actualEntryCount > options.MaxEntryCount)
            {
                throw EntryCountLimit();
            }

            content.Seek(variableLength, SeekOrigin.Current);
            consumedBytes += recordLength;
        }

        if (consumedBytes != centralDirectorySize || actualEntryCount != declaredEntryCount)
        {
            throw new InvalidDataException("ZIP central directory entry count is inconsistent.");
        }

        return actualEntryCount;
    }

    private string ValidateEntryPath(string rawPath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(rawPath)
            || rawPath.StartsWith("/", StringComparison.Ordinal)
            || rawPath.Contains('\\', StringComparison.Ordinal)
            || rawPath.Contains(':', StringComparison.Ordinal)
            || rawPath.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(rawPath) > options.MaxEntryPathBytes)
        {
            throw UnsafePath();
        }

        var path = isDirectory ? rawPath[..^1] : rawPath;
        if (path.Length == 0
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.EndsWith("/", StringComparison.Ordinal))
        {
            throw UnsafePath();
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw UnsafePath();
        }

        return path.Normalize(NormalizationForm.FormC);
    }

    private static void ValidateEntryType(ZipArchiveEntry entry, bool isDirectory)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixRegularFile = 0x8000;
        const int unixDirectory = 0x4000;

        var unixFileType = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        var expectedUnixType = isDirectory ? unixDirectory : unixRegularFile;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);

        if ((unixFileType != 0 && unixFileType != expectedUnixType)
            || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw IntakeFailure(
                "provider-result-archive-special-entry",
                "The Provider result archive contains a link or special file entry.",
                ProviderFailureCategory.Security);
        }
    }

    private void ValidateCompressionRatio(long sizeBytes, long compressedSizeBytes)
    {
        if (sizeBytes == 0)
        {
            return;
        }

        if (compressedSizeBytes <= 0
            || (double)sizeBytes / compressedSizeBytes > options.MaxCompressionRatio)
        {
            throw IntakeFailure(
                "provider-result-archive-compression-ratio",
                "A Provider result archive entry exceeds the configured compression ratio limit.",
                ProviderFailureCategory.Security);
        }
    }

    private static async Task<bool> HasZipSignatureAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        var signature = new byte[4];
        var bytesRead = await content.ReadAtLeastAsync(
            signature,
            signature.Length,
            throwOnEndOfStream: false,
            cancellationToken);

        return bytesRead == signature.Length
            && signature[0] == (byte)'P'
            && signature[1] == (byte)'K'
            && ((signature[2] == 3 && signature[3] == 4)
                || (signature[2] == 5 && signature[3] == 6)
                || (signature[2] == 7 && signature[3] == 8));
    }

    private void ValidateArchiveMediaType(string mediaType)
    {
        var normalized = mediaType.Split(';', 2)[0].Trim();
        if (!AcceptedArchiveMediaTypes.Contains(normalized))
        {
            throw IntakeFailure(
                "provider-result-media-type-unsupported",
                "The Provider result media type is not supported as a ZIP archive.",
                ProviderFailureCategory.Permanent);
        }
    }

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copiedBytes = 0;

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                copiedBytes = checked(copiedBytes + bytesRead);
                if (copiedBytes > expectedSizeBytes)
                {
                    throw new InvalidDataException("Stored Provider result size exceeds its metadata.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (copiedBytes != expectedSizeBytes)
        {
            throw new InvalidDataException("Stored Provider result size does not match its metadata.");
        }
    }

    private async Task<StoredFile> ComputeStoredFileAsync(
        string storageRef,
        Stream content,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long sizeBytes = 0;

        try
        {
            while (true)
            {
                var bytesRead = await content.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                if (sizeBytes > options.MaxArchiveBytes - bytesRead)
                {
                    throw IntakeFailure(
                        "provider-result-too-large",
                        "The stored Provider result exceeds the configured compressed size limit.",
                        ProviderFailureCategory.Permanent);
                }

                sizeBytes += bytesRead;
                hash.AppendData(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (sizeBytes == 0)
        {
            throw IntakeFailure(
                "provider-result-archive-empty",
                "The stored Provider result archive is empty.",
                ProviderFailureCategory.Permanent);
        }

        return new StoredFile(
            storageRef,
            sizeBytes,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static long AddWithLimit(
        long current,
        int increment,
        long limit,
        string errorCode,
        string message)
    {
        if (current > limit - increment)
        {
            throw IntakeFailure(errorCode, message, ProviderFailureCategory.Security);
        }

        return current + increment;
    }

    private static ProviderResultIntakeException UnsafePath() => IntakeFailure(
        "provider-result-archive-unsafe-path",
        "The Provider result archive contains an unsafe entry path.",
        ProviderFailureCategory.Security);

    private static ProviderResultIntakeException EntryCountLimit() => IntakeFailure(
        "provider-result-archive-entry-limit",
        "The Provider result archive exceeds the configured entry count limit.",
        ProviderFailureCategory.Security);

    private static ProviderResultIntakeException IntakeFailure(
        string errorCode,
        string safeMessage,
        ProviderFailureCategory category,
        Exception? innerException = null) => new(
            errorCode,
            safeMessage,
            category,
            innerException);

    private sealed record ArchiveValidation(
        IReadOnlyList<ProviderArchiveEntry> Entries,
        long ExpandedSizeBytes);
}
