using System.Text;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class ParseBundleCommitStoreTests
{
    [Fact]
    public async Task Valid_bundle_is_committed_atomically_and_replays_by_fingerprint()
    {
        await using var database = await BundleTestDatabase.CreateAsync();
        var nowUtc = DateTime.UtcNow;
        var lease = await database.CreateRunningLeaseAsync(nowUtc);
        var bundle = await database.CreateBundleAsync(lease.ParseRunId);
        var equivalentBundle = bundle with
        {
            Pages = bundle.Pages.Reverse().ToArray(),
            ProviderMetadataJson = "{\"model\":\"test-model\",\"providerType\":\"mineru-local\"}",
        };
        Assert.Equal(
            ParseBundleValidator.ComputeFingerprint(bundle),
            ParseBundleValidator.ComputeFingerprint(equivalentBundle));

        ParseBundleCommitResult committed;
        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseBundleCommitStore(dbContext, database.Storage);
            committed = await store.TryCommitAsync(
                lease,
                bundle,
                nowUtc.AddSeconds(2),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(ParseBundleCommitStatus.Committed, committed.Status);

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var parseRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ParseRunStatuses.Succeeded, parseRun.Status);
            Assert.Equal(ParseBundleValidator.CurrentSchemaVersion, parseRun.ResultSchemaVersion);
            Assert.Equal(ParseBundleValidator.ComputeFingerprint(bundle), parseRun.ResultSha256);
            Assert.Equal(bundle.ProviderMetadataJson, parseRun.ProviderMetadataJson);
            Assert.Equal(nowUtc.AddSeconds(2), parseRun.CompletedAtUtc);
            Assert.Null(parseRun.ClaimedBy);
            Assert.Null(parseRun.LeaseExpiresAtUtc);
            Assert.Null(parseRun.Stage);
            Assert.Equal(2, await dbContext.ParsePages.CountAsync(
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, await dbContext.ParseBlocks.CountAsync(
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Single(await dbContext.ParseAssets.ToListAsync(
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Single(await dbContext.ParseArtifacts.ToListAsync(
                cancellationToken: TestContext.Current.CancellationToken));

            var imageBlock = await dbContext.ParseBlocks
                .AsNoTracking()
                .SingleAsync(
                    block => block.AssetId != null,
                    cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, imageBlock.PageNumber);
            Assert.Equal(0.1, imageBlock.BoundingBoxX0);
            Assert.Equal(0.95, imageBlock.Confidence);
        }

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseBundleCommitStore(dbContext, database.Storage);
            var replay = await store.TryCommitAsync(
                lease,
                bundle,
                nowUtc.AddMinutes(1),
                TestContext.Current.CancellationToken);
            var conflictingBundle = bundle with
            {
                ProviderMetadataJson = "{\"providerType\":\"mineru-local\",\"model\":\"different\"}",
            };
            var conflict = await store.TryCommitAsync(
                lease,
                conflictingBundle,
                nowUtc.AddMinutes(1),
                TestContext.Current.CancellationToken);

            Assert.Equal(ParseBundleCommitStatus.AlreadyCommitted, replay.Status);
            Assert.Equal(ParseBundleCommitStatus.Conflict, conflict.Status);
        }
    }

    [Fact]
    public async Task Storage_hash_mismatch_does_not_publish_the_result()
    {
        await using var database = await BundleTestDatabase.CreateAsync();
        var nowUtc = DateTime.UtcNow;
        var lease = await database.CreateRunningLeaseAsync(nowUtc);
        var bundle = await database.CreateBundleAsync(lease.ParseRunId);
        bundle = bundle with
        {
            Artifacts = bundle.Artifacts
                .Select(artifact => artifact with { Sha256 = new string('0', 64) })
                .ToArray(),
        };

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseBundleCommitStore(dbContext, database.Storage);
            var result = await store.TryCommitAsync(
                lease,
                bundle,
                nowUtc.AddSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.Equal(ParseBundleCommitStatus.StorageMismatch, result.Status);
            Assert.Equal("storage-content-mismatch", result.ErrorCode);
        }

        await database.AssertTargetRunHasNoResultAsync(ParseRunStatuses.Running);
    }

    [Fact]
    public async Task Lost_lease_cannot_publish_a_verified_bundle()
    {
        await using var database = await BundleTestDatabase.CreateAsync();
        var nowUtc = DateTime.UtcNow;
        var lease = await database.CreateRunningLeaseAsync(nowUtc);
        var bundle = await database.CreateBundleAsync(lease.ParseRunId);

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == lease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.CancelRequested)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseBundleCommitStore(dbContext, database.Storage);
            var result = await store.TryCommitAsync(
                lease,
                bundle,
                nowUtc.AddSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.Equal(ParseBundleCommitStatus.LeaseLost, result.Status);
        }

        await database.AssertTargetRunHasNoResultAsync(ParseRunStatuses.CancelRequested);
    }

    [Fact]
    public async Task Database_conflict_rolls_back_every_result_row_and_status_change()
    {
        await using var database = await BundleTestDatabase.CreateAsync();
        var nowUtc = DateTime.UtcNow;
        var lease = await database.CreateRunningLeaseAsync(nowUtc);
        var bundle = await database.CreateBundleAsync(lease.ParseRunId);
        var conflictingAssetId = bundle.Assets[0].Id;

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var otherDocument = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "other.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                Sha256 = new string('c', 64),
                StorageRef = "documents/other.pdf",
                CreatedAtUtc = nowUtc,
            };
            var otherRun = new ParseRunEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = otherDocument.Id,
                Status = ParseRunStatuses.Succeeded,
                ProviderType = "test-provider",
                ProviderConfigId = Guid.NewGuid(),
                ProviderConfigVersion = Guid.NewGuid(),
                OptionsJson = "{}",
                SourceMediaType = "application/pdf",
                SubmittedMediaType = "application/pdf",
                MaxAttempts = 1,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                CompletedAtUtc = nowUtc,
            };
            dbContext.Documents.Add(otherDocument);
            dbContext.ParseRuns.Add(otherRun);
            dbContext.ParseAssets.Add(new ParseAssetEntity
            {
                Id = conflictingAssetId,
                ParseRunId = otherRun.Id,
                Name = "existing.png",
                MediaType = "image/png",
                SizeBytes = 1,
                Sha256 = new string('d', 64),
                StorageRef = "results/existing.png",
                CreatedAtUtc = nowUtc,
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseBundleCommitStore(dbContext, database.Storage);
            var result = await store.TryCommitAsync(
                lease,
                bundle,
                nowUtc.AddSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.Equal(ParseBundleCommitStatus.Conflict, result.Status);
        }

        await database.AssertTargetRunHasNoResultAsync(ParseRunStatuses.Running);
    }

    [Fact]
    public async Task Conversion_artifact_must_match_the_entire_persisted_snapshot()
    {
        await using var database = await BundleTestDatabase.CreateAsync();
        var nowUtc = DateTime.UtcNow;
        var lease = await database.CreateRunningLeaseAsync(nowUtc);
        var bundle = await database.CreateBundleAsync(lease.ParseRunId);
        var artifactId = Guid.NewGuid();
        var conversion = new ParseRunConversion(
            "libreoffice",
            "LibreOffice test-version",
            "application/pdf",
            "application/pdf",
            artifactId,
            "normalized.pdf",
            123,
            new string('c', 64),
            $"parse-runs/{lease.ParseRunId:N}/conversions/expected.pdf",
            "pdf");
        var existingFile = bundle.Artifacts[0];
        bundle = bundle with
        {
            Artifacts =
            [
                .. bundle.Artifacts,
                new ParseArtifact(
                    artifactId,
                    ArtifactTypes.NormalizedPdf,
                    "normalized.pdf",
                    "application/pdf",
                    existingFile.SizeBytes,
                    existingFile.Sha256,
                    existingFile.StorageRef,
                    "{\"converterType\":\"different\"}"),
            ],
        };

        await using var dbContext = new StructaDocDbContext(database.Options);
        await dbContext.ParseRuns
            .Where(parseRun => parseRun.Id == lease.ParseRunId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(parseRun => parseRun.ConversionJson, conversion.ToJson()),
            cancellationToken: TestContext.Current.CancellationToken);
        var store = new EfCoreParseBundleCommitStore(dbContext, database.Storage);

        var result = await store.TryCommitAsync(
            lease,
            bundle,
            nowUtc.AddSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(ParseBundleCommitStatus.InvalidBundle, result.Status);
        Assert.Equal("invalid-conversion-artifact", result.ErrorCode);
        await database.AssertTargetRunHasNoResultAsync(ParseRunStatuses.Running);
    }

    [Fact]
    public void Validator_rejects_non_contiguous_blocks_and_sensitive_provider_data()
    {
        var parseRunId = Guid.NewGuid();
        var invalidSequence = new ParseBundle(
            ParseBundleValidator.CurrentSchemaVersion,
            parseRunId,
            [new ParsePage(1)],
            [new ParseBlock(Guid.NewGuid(), 1, 1, "text")],
            [],
            [],
            "{}");
        var sensitiveData = invalidSequence with
        {
            Blocks = [new ParseBlock(
                Guid.NewGuid(),
                0,
                1,
                "text",
                ProviderDataJson: "{\"resultUrl\":\"https://provider.test/result?signature=secret\"}")],
        };

        Assert.Equal(
            "invalid-block-sequence",
            ParseBundleValidator.Validate(invalidSequence).ErrorCode);
        Assert.Equal(
            "sensitive-provider-data",
            ParseBundleValidator.Validate(sensitiveData).ErrorCode);
    }

    private sealed class BundleTestDatabase : IAsyncDisposable
    {
        private readonly string directoryPath;
        private Guid targetParseRunId;

        private BundleTestDatabase(
            string directoryPath,
            DbContextOptions<StructaDocDbContext> options,
            LocalFileStorage storage)
        {
            this.directoryPath = directoryPath;
            Options = options;
            Storage = storage;
        }

        public DbContextOptions<StructaDocDbContext> Options { get; }

        public LocalFileStorage Storage { get; }

        public static async Task<BundleTestDatabase> CreateAsync()
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "structadoc-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var options = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(directoryPath, "structadoc.db")};Pooling=False",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly))
                .Options;
            var storage = new LocalFileStorage(new FileStorageOptions
            {
                Provider = "Local",
                RootPath = Path.Combine(directoryPath, "storage"),
            });
            var database = new BundleTestDatabase(directoryPath, options, storage);

            await using var dbContext = new StructaDocDbContext(options);
            await dbContext.Database.MigrateAsync();
            return database;
        }

        public async Task<ParseRunLease> CreateRunningLeaseAsync(DateTime nowUtc)
        {
            targetParseRunId = Guid.NewGuid();

            await using (var dbContext = new StructaDocDbContext(Options))
            {
                var document = new DocumentEntity
                {
                    Id = Guid.NewGuid(),
                    OriginalFileName = "bundle.pdf",
                    MediaType = "application/pdf",
                    Extension = ".pdf",
                    SizeBytes = 128,
                    Sha256 = new string('a', 64),
                    StorageRef = "documents/bundle.pdf",
                    CreatedAtUtc = nowUtc,
                };
                dbContext.Documents.Add(document);
                dbContext.ParseRuns.Add(new ParseRunEntity
                {
                    Id = targetParseRunId,
                    DocumentId = document.Id,
                    Status = ParseRunStatuses.Queued,
                    ProviderType = "test-provider",
                    ProviderConfigId = Guid.NewGuid(),
                    ProviderConfigVersion = Guid.NewGuid(),
                    OptionsJson = "{}",
                    SourceMediaType = "application/pdf",
                    SubmittedMediaType = "application/pdf",
                    MaxAttempts = 3,
                    NextAttemptAtUtc = nowUtc,
                    CreatedAtUtc = nowUtc,
                });
                await dbContext.SaveChangesAsync();
            }

            ParseRunLease claimedLease;
            await using (var dbContext = new StructaDocDbContext(Options))
            {
                var leaseStore = new EfCoreParseRunLeaseStore(dbContext);
                claimedLease = Assert.IsType<ParseRunLease>(await leaseStore.TryClaimNextAsync(
                    "bundle-test-worker",
                    nowUtc,
                    TimeSpan.FromMinutes(5)));
            }

            await using (var dbContext = new StructaDocDbContext(Options))
            {
                var stateStore = new EfCoreParseRunStateStore(dbContext);
                return Assert.IsType<ParseRunLease>(await stateStore.TryStartAsync(
                    claimedLease,
                    ParseRunStages.Persisting,
                    nowUtc.AddSeconds(1)));
            }
        }

        public async Task<ParseBundle> CreateBundleAsync(Guid parseRunId)
        {
            var image = await WriteAsync(
                $"results/{parseRunId:N}/image.png",
                "test-image-content");
            var markdown = await WriteAsync(
                $"results/{parseRunId:N}/full.md",
                "# Test\n\nParsed content.");
            var assetId = Guid.NewGuid();

            return new ParseBundle(
                ParseBundleValidator.CurrentSchemaVersion,
                parseRunId,
                [
                    new ParsePage(1, 1000, 1400, "pixel", "{\"sourcePage\":0}"),
                    new ParsePage(2, 1000, 1400, "pixel"),
                ],
                [
                    new ParseBlock(
                        Guid.NewGuid(),
                        0,
                        1,
                        "title",
                        Content: "Test",
                        ContentFormat: "plain"),
                    new ParseBlock(
                        Guid.NewGuid(),
                        1,
                        1,
                        "image",
                        Subtype: "figure",
                        BoundingBox: new NormalizedBoundingBox(0.1, 0.2, 0.8, 0.9),
                        Confidence: 0.95,
                        AssetId: assetId,
                        ProviderDataJson: "{\"providerBlockId\":\"block-1\"}"),
                ],
                [
                    new ParseAsset(
                        assetId,
                        "image-1.png",
                        "image/png",
                        image.SizeBytes,
                        image.Sha256,
                        image.StorageRef,
                        640,
                        480),
                ],
                [
                    new ParseArtifact(
                        Guid.NewGuid(),
                        ArtifactTypes.Markdown,
                        "full.md",
                        "text/markdown",
                        markdown.SizeBytes,
                        markdown.Sha256,
                        markdown.StorageRef,
                        "{\"role\":\"primary\"}"),
                ],
                "{\"providerType\":\"mineru-local\",\"model\":\"test-model\"}");
        }

        public async Task AssertTargetRunHasNoResultAsync(string expectedStatus)
        {
            await using var dbContext = new StructaDocDbContext(Options);
            var parseRun = await dbContext.ParseRuns
                .AsNoTracking()
                .SingleAsync(item => item.Id == targetParseRunId);
            Assert.Equal(expectedStatus, parseRun.Status);
            Assert.Null(parseRun.ResultSchemaVersion);
            Assert.Null(parseRun.ResultSha256);
            Assert.Null(parseRun.ProviderMetadataJson);
            Assert.Empty(await dbContext.ParsePages.Where(item => item.ParseRunId == targetParseRunId).ToListAsync());
            Assert.Empty(await dbContext.ParseBlocks.Where(item => item.ParseRunId == targetParseRunId).ToListAsync());
            Assert.Empty(await dbContext.ParseAssets.Where(item => item.ParseRunId == targetParseRunId).ToListAsync());
            Assert.Empty(await dbContext.ParseArtifacts.Where(item => item.ParseRunId == targetParseRunId).ToListAsync());
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(directoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }

        private async Task<StructaDoc.Application.Storage.StoredFile> WriteAsync(
            string storageRef,
            string content)
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            return await Storage.WriteAsync(storageRef, stream, 1024 * 1024);
        }
    }
}
