using System.Buffers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.ParseRuns;

public sealed class EfCoreParseBundleCommitStore(
    StructaDocDbContext dbContext,
    IFileStorage fileStorage) : IParseBundleCommitStore
{
    private const int VerificationBufferSize = 64 * 1024;

    public async Task<ParseBundleCommitResult> TryCommitAsync(
        ParseRunLease currentLease,
        ParseBundle bundle,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateUtc(nowUtc);

        if (bundle.ParseRunId != currentLease.ParseRunId)
        {
            return InvalidBundle(
                "parse-run-mismatch",
                "The Parse Bundle does not belong to the leased Parse Run.");
        }

        if (bundle.Pages is null
            || bundle.Blocks is null
            || bundle.Assets is null
            || bundle.Artifacts is null)
        {
            return InvalidBundle(
                "missing-collection",
                "Parse Bundle collections cannot be null.");
        }

        bundle = bundle with
        {
            Pages = bundle.Pages.ToArray(),
            Blocks = bundle.Blocks.ToArray(),
            Assets = bundle.Assets.ToArray(),
            Artifacts = bundle.Artifacts.ToArray(),
        };

        var validation = ParseBundleValidator.Validate(bundle);
        if (!validation.IsValid)
        {
            return InvalidBundle(validation.ErrorCode!, validation.ErrorMessage!);
        }

        var fingerprint = ParseBundleValidator.ComputeFingerprint(bundle);
        var existingResult = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun => parseRun.Id == bundle.ParseRunId)
            .Select(parseRun => new
            {
                parseRun.Status,
                parseRun.ResultSha256,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (existingResult?.Status == ParseRunStatuses.Succeeded)
        {
            return string.Equals(existingResult.ResultSha256, fingerprint, StringComparison.Ordinal)
                ? new(ParseBundleCommitStatus.AlreadyCommitted)
                : new(
                    ParseBundleCommitStatus.Conflict,
                    "result-conflict",
                    "The Parse Run already contains a different committed result.");
        }

        var storageVerification = await VerifyStorageAsync(bundle, cancellationToken);
        if (storageVerification is not null)
        {
            return storageVerification;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var parseRun = await dbContext.ParseRuns
                .SingleOrDefaultAsync(item => item.Id == bundle.ParseRunId, cancellationToken);
            if (parseRun is null)
            {
                return new(
                    ParseBundleCommitStatus.Conflict,
                    "parse-run-not-found",
                    "The Parse Run no longer exists.");
            }

            if (parseRun.Status == ParseRunStatuses.Succeeded)
            {
                return string.Equals(parseRun.ResultSha256, fingerprint, StringComparison.Ordinal)
                    ? new(ParseBundleCommitStatus.AlreadyCommitted)
                    : new(
                        ParseBundleCommitStatus.Conflict,
                        "result-conflict",
                        "The Parse Run already contains a different committed result.");
            }

            if (parseRun.Status != ParseRunStatuses.Running
                || parseRun.ClaimedBy != currentLease.WorkerId
                || parseRun.ConcurrencyVersion != currentLease.ConcurrencyVersion
                || parseRun.LeaseExpiresAtUtc <= nowUtc)
            {
                return new(
                    ParseBundleCommitStatus.LeaseLost,
                    "lease-lost",
                    "The Worker no longer holds the Parse Run lease.");
            }

            if (!ValidateConversionArtifact(parseRun.ConversionJson, bundle.Artifacts))
            {
                return InvalidBundle(
                    "invalid-conversion-artifact",
                    "The conversion snapshot does not reference a normalized PDF Artifact in this Bundle.");
            }

            if (await HasExistingResultRowsAsync(bundle.ParseRunId, cancellationToken))
            {
                return new(
                    ParseBundleCommitStatus.Conflict,
                    "partial-result-conflict",
                    "The running Parse Run already contains result rows.");
            }

            dbContext.ParsePages.AddRange(bundle.Pages.Select(page => new ParsePageEntity
            {
                ParseRunId = bundle.ParseRunId,
                Number = page.Number,
                Width = page.Width,
                Height = page.Height,
                Unit = page.Unit,
                SourceLocatorJson = page.SourceLocatorJson,
            }));
            dbContext.ParseAssets.AddRange(bundle.Assets.Select(asset => new ParseAssetEntity
            {
                Id = asset.Id,
                ParseRunId = bundle.ParseRunId,
                Name = asset.Name,
                MediaType = asset.MediaType,
                SizeBytes = asset.SizeBytes,
                Sha256 = asset.Sha256,
                StorageRef = asset.StorageRef,
                Width = asset.Width,
                Height = asset.Height,
                CreatedAtUtc = nowUtc,
            }));
            dbContext.ParseArtifacts.AddRange(bundle.Artifacts.Select(artifact => new ParseArtifactEntity
            {
                Id = artifact.Id,
                ParseRunId = bundle.ParseRunId,
                Type = artifact.Type,
                Name = artifact.Name,
                MediaType = artifact.MediaType,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256,
                StorageRef = artifact.StorageRef,
                MetadataJson = artifact.MetadataJson,
                CreatedAtUtc = nowUtc,
            }));
            dbContext.ParseBlocks.AddRange(bundle.Blocks.Select(block => new ParseBlockEntity
            {
                Id = block.Id,
                ParseRunId = bundle.ParseRunId,
                Sequence = block.Sequence,
                PageNumber = block.PageNumber,
                Type = block.Type,
                Subtype = block.Subtype,
                Content = block.Content,
                ContentFormat = block.ContentFormat,
                BoundingBoxX0 = block.BoundingBox?.X0,
                BoundingBoxY0 = block.BoundingBox?.Y0,
                BoundingBoxX1 = block.BoundingBox?.X1,
                BoundingBoxY1 = block.BoundingBox?.Y1,
                Confidence = block.Confidence,
                AssetId = block.AssetId,
                SourceLocatorJson = block.SourceLocatorJson,
                ProviderDataJson = block.ProviderDataJson,
            }));

            parseRun.Status = ParseRunStatuses.Succeeded;
            parseRun.Stage = null;
            parseRun.ResultSchemaVersion = bundle.SchemaVersion;
            parseRun.ResultSha256 = fingerprint;
            parseRun.ProviderMetadataJson = bundle.ProviderMetadataJson;
            parseRun.ErrorCode = null;
            parseRun.ErrorMessage = null;
            parseRun.ClaimedBy = null;
            parseRun.LeaseExpiresAtUtc = null;
            parseRun.NextAttemptAtUtc = nowUtc;
            parseRun.CompletedAtUtc = nowUtc;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ParseBundleCommitStatus.Committed);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return new(
                ParseBundleCommitStatus.LeaseLost,
                "lease-lost",
                "The Parse Run changed while committing its result.");
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return new(
                ParseBundleCommitStatus.Conflict,
                "result-conflict",
                "The Parse Bundle conflicts with existing result data.");
        }
    }

    private async Task<ParseBundleCommitResult?> VerifyStorageAsync(
        ParseBundle bundle,
        CancellationToken cancellationToken)
    {
        var expectedFiles = new Dictionary<string, (long SizeBytes, string Sha256)>(StringComparer.Ordinal);
        foreach (var file in bundle.Assets
                     .Select(asset => (asset.StorageRef, asset.SizeBytes, asset.Sha256))
                     .Concat(bundle.Artifacts.Select(artifact =>
                         (artifact.StorageRef, artifact.SizeBytes, artifact.Sha256))))
        {
            if (expectedFiles.TryGetValue(file.StorageRef, out var existing)
                && (existing.SizeBytes != file.SizeBytes
                    || !string.Equals(existing.Sha256, file.Sha256, StringComparison.Ordinal)))
            {
                return new(
                    ParseBundleCommitStatus.StorageMismatch,
                    "storage-reference-conflict",
                    "One storage reference has conflicting expected metadata.");
            }

            expectedFiles[file.StorageRef] = (file.SizeBytes, file.Sha256);
        }

        foreach (var (storageRef, expected) in expectedFiles)
        {
            try
            {
                await using var content = await fileStorage.OpenReadAsync(storageRef, cancellationToken);
                if (!await MatchesAsync(content, expected.SizeBytes, expected.Sha256, cancellationToken))
                {
                    return new(
                        ParseBundleCommitStatus.StorageMismatch,
                        "storage-content-mismatch",
                        "A stored result does not match its expected size and SHA-256 hash.");
                }
            }
            catch (FileNotFoundException)
            {
                return new(
                    ParseBundleCommitStatus.StorageMismatch,
                    "storage-object-missing",
                    "A stored result object is missing.");
            }
            catch (DirectoryNotFoundException)
            {
                return new(
                    ParseBundleCommitStatus.StorageMismatch,
                    "storage-object-missing",
                    "A stored result object is missing.");
            }
            catch (ArgumentException)
            {
                return InvalidBundle(
                    "invalid-storage-reference",
                    "A result storage reference is invalid.");
            }
        }

        return null;
    }

    private async Task<bool> HasExistingResultRowsAsync(
        Guid parseRunId,
        CancellationToken cancellationToken) =>
        await dbContext.ParsePages.AnyAsync(page => page.ParseRunId == parseRunId, cancellationToken)
        || await dbContext.ParseBlocks.AnyAsync(block => block.ParseRunId == parseRunId, cancellationToken)
        || await dbContext.ParseAssets.AnyAsync(asset => asset.ParseRunId == parseRunId, cancellationToken)
        || await dbContext.ParseArtifacts.AnyAsync(artifact => artifact.ParseRunId == parseRunId, cancellationToken);

    private static bool ValidateConversionArtifact(
        string? conversionJson,
        IReadOnlyList<ParseArtifact> artifacts)
    {
        if (conversionJson is null)
        {
            return true;
        }

        try
        {
            var expected = ParseRunConversion.FromJson(conversionJson).ToArtifact();
            return artifacts.Any(artifact => artifact == expected);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> MatchesAsync(
        Stream content,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(VerificationBufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long sizeBytes = 0;

            while (true)
            {
                var bytesRead = await content.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                sizeBytes = checked(sizeBytes + bytesRead);
                if (sizeBytes > expectedSizeBytes)
                {
                    return false;
                }

                hash.AppendData(buffer, 0, bytesRead);
            }

            return sizeBytes == expectedSizeBytes
                && string.Equals(
                    Convert.ToHexString(hash.GetHashAndReset()),
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Parse Bundle timestamps must use UTC.", nameof(value));
        }
    }

    private static ParseBundleCommitResult InvalidBundle(string code, string message) =>
        new(ParseBundleCommitStatus.InvalidBundle, code, message);
}
