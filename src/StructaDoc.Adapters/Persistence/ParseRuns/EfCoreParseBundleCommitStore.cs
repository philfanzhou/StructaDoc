using System.Buffers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Adapters.Persistence.ParseRuns;

public sealed class EfCoreParseBundleCommitStore(
    StructaDocDbContext dbContext,
    IFileStorage fileStorage,
    TimeProvider timeProvider) : IParseBundleCommitStore
{
    private const int VerificationBufferSize = 64 * 1024;

    public async Task<ParseBundleVerificationResult> VerifyBundleAsync(
        ParseBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (bundle.Pages is null
            || bundle.Blocks is null
            || bundle.Assets is null
            || bundle.Artifacts is null)
        {
            return VerificationInvalidBundle(
                "missing-collection",
                "Parse Bundle collections cannot be null.");
        }

        // Freeze the snapshot so later caller mutations cannot change what this verification
        // certified. The commit phase still rebinds the snapshot to its receipt.
        var snapshot = bundle with
        {
            Pages = bundle.Pages.ToArray(),
            Blocks = bundle.Blocks.ToArray(),
            Assets = bundle.Assets.ToArray(),
            Artifacts = bundle.Artifacts.ToArray(),
        };

        var validation = ParseBundleValidator.Validate(snapshot);
        if (!validation.IsValid)
        {
            return VerificationInvalidBundle(validation.ErrorCode!, validation.ErrorMessage!);
        }

        var fingerprint = ParseBundleValidator.ComputeFingerprint(snapshot);

        // A finished run needs no storage I/O: an identical fingerprint replays as committed and
        // a different one conflicts. The guarded transaction rechecks this decision authoritatively.
        var existingResult = await dbContext.ParseRuns
            .AsNoTracking()
            .Where(parseRun => parseRun.Id == snapshot.ParseRunId)
            .Select(parseRun => new
            {
                parseRun.Status,
                parseRun.ResultSha256,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (existingResult?.Status == ParseRunStatuses.Succeeded)
        {
            return string.Equals(existingResult.ResultSha256, fingerprint, StringComparison.Ordinal)
                ? new(ParseBundleVerificationStatus.AlreadyCommitted)
                : new(
                    ParseBundleVerificationStatus.Conflict,
                    ErrorCode: "result-conflict",
                    ErrorMessage: "The Parse Run already contains a different committed result.");
        }

        return await VerifyStorageAsync(snapshot, fingerprint, cancellationToken);
    }

    public async Task<ParseBundleCommitResult> TryCommitAsync(
        ParseRunLease currentLease,
        VerifiedParseBundle verifiedBundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ArgumentNullException.ThrowIfNull(verifiedBundle);
        ArgumentNullException.ThrowIfNull(verifiedBundle.Bundle);

        var bundle = verifiedBundle.Bundle;

        if (bundle.ParseRunId != currentLease.ParseRunId)
        {
            return InvalidBundle(
                "parse-run-mismatch",
                "The Parse Bundle does not belong to the leased Parse Run.");
        }

        // Bind the snapshot to its receipt: the fingerprint and the verified object metadata are
        // re-derived from the snapshot, so data changed after verification — or a receipt assembled
        // around other claims — cannot enter the guarded phase.
        if (!ParseBundleValidator.Validate(bundle).IsValid)
        {
            return ReceiptMismatch();
        }

        var fingerprint = ParseBundleValidator.ComputeFingerprint(bundle);
        if (!TryBuildExpectedStorage(bundle, out var expectedStorage)
            || !string.Equals(fingerprint, verifiedBundle.Fingerprint, StringComparison.Ordinal)
            || !MatchesVerifiedStorage(expectedStorage, verifiedBundle.VerifiedStorage))
        {
            return ReceiptMismatch();
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            // Read the clock inside the retried operation. The guarded mutation — and every
            // execution-strategy retry after a transient failure — decides lease expiry with the
            // current time, never a timestamp captured before the storage verification ran.
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
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
                    Label = page.Label,
                    LabelSource = page.LabelSource,
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
                    Caption = block.Caption,
                    Footnote = block.Footnote,
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
        });
    }

    private async Task<ParseBundleVerificationResult> VerifyStorageAsync(
        ParseBundle snapshot,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (!TryBuildExpectedStorage(snapshot, out var expectedStorage))
        {
            return new(
                ParseBundleVerificationStatus.StorageMismatch,
                ErrorCode: "storage-reference-conflict",
                ErrorMessage: "One storage reference has conflicting expected metadata.");
        }

        foreach (var (storageRef, expected) in expectedStorage)
        {
            try
            {
                await using var content = await fileStorage.OpenReadAsync(storageRef, cancellationToken);
                if (!await MatchesAsync(content, expected.SizeBytes, expected.Sha256, cancellationToken))
                {
                    return new(
                        ParseBundleVerificationStatus.StorageMismatch,
                        ErrorCode: "storage-content-mismatch",
                        ErrorMessage: "A stored result does not match its expected size and SHA-256 hash.");
                }
            }
            catch (FileNotFoundException)
            {
                return new(
                    ParseBundleVerificationStatus.StorageMismatch,
                    ErrorCode: "storage-object-missing",
                    ErrorMessage: "A stored result object is missing.");
            }
            catch (DirectoryNotFoundException)
            {
                return new(
                    ParseBundleVerificationStatus.StorageMismatch,
                    ErrorCode: "storage-object-missing",
                    ErrorMessage: "A stored result object is missing.");
            }
            catch (ArgumentException)
            {
                return VerificationInvalidBundle(
                    "invalid-storage-reference",
                    "A result storage reference is invalid.");
            }
        }

        return new(
            ParseBundleVerificationStatus.Verified,
            VerifiedBundle: new VerifiedParseBundle(snapshot, fingerprint, expectedStorage));
    }

    /// <summary>
    /// Derives the verified object metadata from a snapshot. A storage reference repeated with
    /// conflicting expected metadata fails derivation; equal repetitions are one verified object.
    /// </summary>
    private static bool TryBuildExpectedStorage(
        ParseBundle bundle,
        out Dictionary<string, VerifiedStorageObject> expectedStorage)
    {
        expectedStorage = new Dictionary<string, VerifiedStorageObject>(StringComparer.Ordinal);

        foreach (var file in bundle.Assets
                     .Select(asset => (asset.StorageRef, asset.SizeBytes, asset.Sha256))
                     .Concat(bundle.Artifacts.Select(artifact =>
                         (artifact.StorageRef, artifact.SizeBytes, artifact.Sha256))))
        {
            if (expectedStorage.TryGetValue(file.StorageRef, out var existing)
                && (existing.SizeBytes != file.SizeBytes
                    || !string.Equals(existing.Sha256, file.Sha256, StringComparison.Ordinal)))
            {
                return false;
            }

            expectedStorage[file.StorageRef] = new VerifiedStorageObject(file.SizeBytes, file.Sha256);
        }

        return true;
    }

    private static bool MatchesVerifiedStorage(
        Dictionary<string, VerifiedStorageObject> expectedStorage,
        IReadOnlyDictionary<string, VerifiedStorageObject>? verifiedStorage)
    {
        if (verifiedStorage is null
            || verifiedStorage.Count != expectedStorage.Count)
        {
            return false;
        }

        foreach (var (storageRef, expected) in expectedStorage)
        {
            if (!verifiedStorage.TryGetValue(storageRef, out var verified)
                || verified.SizeBytes != expected.SizeBytes
                || !string.Equals(verified.Sha256, expected.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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

    private static ParseBundleVerificationResult VerificationInvalidBundle(string code, string message) =>
        new(ParseBundleVerificationStatus.InvalidBundle, ErrorCode: code, ErrorMessage: message);

    private static ParseBundleCommitResult ReceiptMismatch() =>
        InvalidBundle(
            "verification-receipt-mismatch",
            "The verified bundle snapshot does not match its verification receipt.");

    private static ParseBundleCommitResult InvalidBundle(string code, string message) =>
        new(ParseBundleCommitStatus.InvalidBundle, code, message);
}
