using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Resources;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Resources;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;
using StructaDoc.Migrations.Sqlite;
using StructaDoc.Testing.Persistence;

namespace StructaDoc.Persistence.Tests;

public sealed class ResourceDeletionServiceTests
{
    [Theory]
    [InlineData(DeletionTarget.Document)]
    [InlineData(DeletionTarget.ParseRun)]
    public async Task Invalid_conversion_snapshot_refuses_deletion_before_mutating_lifecycle(
        DeletionTarget target)
    {
        await using var database = await DeletionTestDatabase.CreateAsync(
            ConversionSnapshot.Invalid);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                RequestDeletionAsync(target, dbContext, database, cancellationToken));

            Assert.Equal(
                $"The persisted conversion snapshot for Parse Run '{database.ParseRunId:D}' "
                    + "is invalid. Restore or repair this record before requesting deletion again.",
                exception.Message);
            Assert.DoesNotContain(
                DeletionTestDatabase.PrivateStorageRef,
                exception.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(
                ResourceLifecycleStates.Active,
                dbContext.ParseRuns.Local.Single().LifecycleState);
            if (target == DeletionTarget.Document)
            {
                Assert.Equal(
                    ResourceLifecycleStates.Active,
                    dbContext.Documents.Local.Single().LifecycleState);
            }

            Assert.Empty(dbContext.CleanupJobs.Local);
        }

        await using var verification = new StructaDocDbContext(database.Options);
        var document = await verification.Documents
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        var parseRun = await verification.ParseRuns
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        Assert.Equal(ResourceLifecycleStates.Active, document.LifecycleState);
        Assert.Null(document.DeletionRequestedAtUtc);
        Assert.Equal(ResourceLifecycleStates.Active, parseRun.LifecycleState);
        Assert.Null(parseRun.DeletionRequestedAtUtc);
        Assert.Empty(await verification.CleanupJobs.AsNoTracking().ToListAsync(cancellationToken));
    }

    [Theory]
    [InlineData(DeletionTarget.Document)]
    [InlineData(DeletionTarget.ParseRun)]
    public async Task Valid_conversion_snapshot_preserves_complete_ordinally_distinct_refs(
        DeletionTarget target)
    {
        await using var database = await DeletionTestDatabase.CreateAsync(
            ConversionSnapshot.Valid);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var result = await RequestDeletionAsync(
                target,
                dbContext,
                database,
                cancellationToken);

            Assert.Equal(ResourceDeletionStatus.Accepted, result.Status);
            Assert.NotNull(result.CleanupJobId);
        }

        await using var verification = new StructaDocDbContext(database.Options);
        var job = await verification.CleanupJobs.AsNoTracking().SingleAsync(cancellationToken);
        var refs = JsonSerializer.Deserialize<string[]>(job.StorageRefsJson);
        Assert.Equal(database.ExpectedStorageRefs(target), refs);
        Assert.Equal(
            1,
            refs!.Count(value => string.Equals(
                value,
                database.ConversionStorageRef,
                StringComparison.Ordinal)));
        Assert.Equal(
            target == DeletionTarget.Document
                ? ResourceLifecycleStates.DeletionPending
                : ResourceLifecycleStates.Active,
            await verification.Documents
                .Select(document => document.LifecycleState)
                .SingleAsync(cancellationToken));
        Assert.Equal(
            ResourceLifecycleStates.DeletionPending,
            await verification.ParseRuns
                .Select(parseRun => parseRun.LifecycleState)
                .SingleAsync(cancellationToken));
    }

    [Theory]
    [InlineData(DeletionTarget.Document)]
    [InlineData(DeletionTarget.ParseRun)]
    public async Task Null_conversion_snapshot_enqueues_only_non_conversion_refs(
        DeletionTarget target)
    {
        await using var database = await DeletionTestDatabase.CreateAsync(
            ConversionSnapshot.None);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var result = await RequestDeletionAsync(
                target,
                dbContext,
                database,
                cancellationToken);

            Assert.Equal(ResourceDeletionStatus.Accepted, result.Status);
        }

        await using var verification = new StructaDocDbContext(database.Options);
        var refsJson = await verification.CleanupJobs
            .Select(job => job.StorageRefsJson)
            .SingleAsync(cancellationToken);
        Assert.Equal(
            database.ExpectedStorageRefs(target),
            JsonSerializer.Deserialize<string[]>(refsJson));
    }

    [Theory]
    [InlineData(DeletionTarget.Document)]
    [InlineData(DeletionTarget.ParseRun)]
    public async Task Multiple_storage_collections_are_projected_with_linear_row_reads(
        DeletionTarget target)
    {
        var commandCounter = new DbCommandCounterInterceptor();
        await using var database = await DeletionTestDatabase.CreateAsync(
            ConversionSnapshot.Valid,
            commandCounter,
            parseRunCount: 3,
            assetsPerRun: 4,
            artifactsPerRun: 4,
            segmentsPerRun: 4);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            using var commandScope = commandCounter.BeginScope();
            var result = await RequestDeletionAsync(
                target,
                dbContext,
                database,
                cancellationToken);

            Assert.Equal(ResourceDeletionStatus.Accepted, result.Status);
            Assert.Equal(database.ExpectedReadRows(target), commandScope.RowCount);
        }

        await using var verification = new StructaDocDbContext(database.Options);
        var storageRefsJson = await verification.CleanupJobs
            .AsNoTracking()
            .Select(job => job.StorageRefsJson)
            .SingleAsync(cancellationToken);
        Assert.Equal(
            JsonSerializer.Serialize(database.ExpectedStorageRefs(target)),
            storageRefsJson);
    }

    private static Task<ResourceDeletionResult> RequestDeletionAsync(
        DeletionTarget target,
        StructaDocDbContext dbContext,
        DeletionTestDatabase database,
        CancellationToken cancellationToken)
    {
        var service = new EfCoreResourceDeletionService(dbContext);
        return target switch
        {
            DeletionTarget.Document => service.RequestDocumentDeletionAsync(
                database.DocumentId,
                ResourceAccessContext.System,
                DateTime.UtcNow,
                cancellationToken),
            DeletionTarget.ParseRun => service.RequestParseRunDeletionAsync(
                database.ParseRunId,
                ResourceAccessContext.System,
                DateTime.UtcNow,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    public enum DeletionTarget
    {
        Document,
        ParseRun,
    }

    private enum ConversionSnapshot
    {
        None,
        Valid,
        Invalid,
    }

    private sealed class DeletionTestDatabase : IAsyncDisposable
    {
        private const string PayloadSha256 =
            "239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426952f9b852d5a935e5";

        private DeletionTestDatabase(
            string directory,
            DbContextOptions<StructaDocDbContext> options,
            Guid documentId,
            IReadOnlyList<ParseRunFixture> parseRuns)
        {
            Directory = directory;
            Options = options;
            DocumentId = documentId;
            ParseRuns = parseRuns;
        }

        public const string PrivateStorageRef = "private/conversion/storage-ref.pdf";

        private string Directory { get; }
        private IReadOnlyList<ParseRunFixture> ParseRuns { get; }
        public DbContextOptions<StructaDocDbContext> Options { get; }
        public Guid DocumentId { get; }
        public Guid ParseRunId => ParseRuns[0].Id;
        public string DocumentStorageRef => $"documents/{DocumentId:N}/original.pdf";
        public string ConversionStorageRef => ParseRuns[0].ConversionStorageRef!;

        public static async Task<DeletionTestDatabase> CreateAsync(
            ConversionSnapshot conversionSnapshot,
            DbCommandCounterInterceptor? commandCounter = null,
            int parseRunCount = 1,
            int assetsPerRun = 1,
            int artifactsPerRun = 1,
            int segmentsPerRun = 1)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "structadoc-deletion-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(directory, "deletion.db")};Pooling=False",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly));
            if (commandCounter is not null)
            {
                optionsBuilder.AddInterceptors(commandCounter);
            }

            var options = optionsBuilder.Options;
            var documentId = Guid.NewGuid();
            var parseRuns = Enumerable.Range(0, parseRunCount)
                .Select(_ => CreateParseRunFixture(
                    conversionSnapshot,
                    assetsPerRun,
                    artifactsPerRun,
                    segmentsPerRun))
                .ToArray();
            var database = new DeletionTestDatabase(
                directory,
                options,
                documentId,
                parseRuns);
            var nowUtc = DateTime.UtcNow;
            var cancellationToken = TestContext.Current.CancellationToken;

            await using var dbContext = new StructaDocDbContext(options);
            await dbContext.Database.MigrateAsync(cancellationToken);
            dbContext.Documents.Add(new DocumentEntity
            {
                Id = database.DocumentId,
                OriginalFileName = "deletion.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 7,
                Sha256 = PayloadSha256,
                StorageRef = database.DocumentStorageRef,
                CreatedAtUtc = nowUtc,
            });

            foreach (var parseRun in parseRuns)
            {
                dbContext.ParseRuns.Add(new ParseRunEntity
                {
                    Id = parseRun.Id,
                    DocumentId = database.DocumentId,
                    Status = ParseRunStatuses.Succeeded,
                    ProviderType = "test-provider",
                    ProviderConfigId = Guid.NewGuid(),
                    ProviderConfigVersion = Guid.NewGuid(),
                    OptionsJson = "{}",
                    SourceMediaType = "application/pdf",
                    SubmittedMediaType = "application/pdf",
                    ConversionJson = conversionSnapshot switch
                    {
                        ConversionSnapshot.None => null,
                        ConversionSnapshot.Valid => new ParseRunConversion(
                            "libreoffice",
                            "24.8",
                            "application/pdf",
                            "application/pdf",
                            parseRun.ConversionArtifactId,
                            "normalized.pdf",
                            7,
                            PayloadSha256,
                            parseRun.ConversionStorageRef!,
                            "pdf").ToJson(),
                        ConversionSnapshot.Invalid =>
                            $"{{\"storageRef\":\"{PrivateStorageRef}\"",
                        _ => throw new ArgumentOutOfRangeException(nameof(conversionSnapshot)),
                    },
                    AttemptCount = 1,
                    MaxAttempts = 3,
                    NextAttemptAtUtc = nowUtc,
                    CreatedAtUtc = nowUtc,
                    CompletedAtUtc = nowUtc,
                });

                dbContext.ParseAssets.AddRange(parseRun.Assets.Select(asset =>
                    new ParseAssetEntity
                    {
                        Id = asset.Id,
                        ParseRunId = parseRun.Id,
                        Name = $"image-{asset.Index:D4}.png",
                        MediaType = "image/png",
                        SizeBytes = 7,
                        Sha256 = PayloadSha256,
                        StorageRef = asset.StorageRef,
                        CreatedAtUtc = nowUtc,
                    }));
                dbContext.ParseArtifacts.AddRange(parseRun.Artifacts.Select(artifact =>
                    new ParseArtifactEntity
                    {
                        Id = artifact.Id,
                        ParseRunId = parseRun.Id,
                        Type = artifact.Index == 0 ? "normalized-pdf" : "test-artifact",
                        Name = artifact.Index == 0
                            ? "normalized.pdf"
                            : $"artifact-{artifact.Index:D4}.bin",
                        MediaType = artifact.Index == 0
                            ? "application/pdf"
                            : "application/octet-stream",
                        SizeBytes = 7,
                        Sha256 = PayloadSha256,
                        StorageRef = artifact.StorageRef,
                        CreatedAtUtc = nowUtc,
                    }));
                dbContext.ParseSegments.AddRange(parseRun.Segments.Select(segment =>
                    new ParseSegmentEntity
                    {
                        Id = segment.Id,
                        ParseRunId = parseRun.Id,
                        Index = segment.Index,
                        StartPage = segment.Index + 1,
                        EndPage = segment.Index + 1,
                        StorageRef = segment.StorageRef,
                        SizeBytes = 7,
                        Sha256 = PayloadSha256,
                        Status = ParseRunStatuses.Succeeded,
                        UpdatedAtUtc = nowUtc,
                    }));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return database;
        }

        public IReadOnlyList<string> ExpectedStorageRefs(DeletionTarget target)
        {
            var parseRuns = target == DeletionTarget.Document
                ? ParseRuns.OrderBy(parseRun => parseRun.Id)
                : ParseRuns.Where(parseRun => parseRun.Id == ParseRunId);
            var storageRefs = new List<string>();
            foreach (var parseRun in parseRuns)
            {
                storageRefs.AddRange(parseRun.Assets
                    .OrderBy(asset => asset.Id)
                    .Select(asset => asset.StorageRef));
                storageRefs.AddRange(parseRun.Artifacts
                    .OrderBy(artifact => artifact.Id)
                    .Select(artifact => artifact.StorageRef));
                storageRefs.Add($"parse-runs/{parseRun.Id:N}/provider/result.zip");
                foreach (var segment in parseRun.Segments.OrderBy(segment => segment.Id))
                {
                    storageRefs.Add(segment.StorageRef);
                    storageRefs.Add($"parse-runs/{segment.Id:N}/provider/result.zip");
                }

                if (parseRun.ConversionStorageRef is not null)
                {
                    storageRefs.Add(parseRun.ConversionStorageRef);
                }
            }

            if (target == DeletionTarget.Document)
            {
                storageRefs.Add(DocumentStorageRef);
            }

            return storageRefs.Distinct(StringComparer.Ordinal).ToArray();
        }

        public long ExpectedReadRows(DeletionTarget target)
        {
            var parseRuns = target == DeletionTarget.Document
                ? ParseRuns
                : ParseRuns.Where(parseRun => parseRun.Id == ParseRunId).ToArray();
            var trackedGraphRows = target == DeletionTarget.Document
                ? parseRuns.Count
                : 1;
            var projectionRows = parseRuns.Count
                + parseRuns.Sum(parseRun => parseRun.Assets.Count)
                + parseRuns.Sum(parseRun => parseRun.Artifacts.Count)
                + parseRuns.Sum(parseRun => parseRun.Segments.Count);
            var mutationRows = target == DeletionTarget.Document
                ? parseRuns.Count + 1
                : 1;
            return trackedGraphRows + projectionRows + mutationRows;
        }

        private static ParseRunFixture CreateParseRunFixture(
            ConversionSnapshot conversionSnapshot,
            int assetsPerRun,
            int artifactsPerRun,
            int segmentsPerRun)
        {
            var parseRunId = Guid.NewGuid();
            var conversionArtifactId = Guid.NewGuid();
            var conversionStorageRef = conversionSnapshot == ConversionSnapshot.Valid
                ? $"parse-runs/{parseRunId:N}/conversions/normalized.pdf"
                : null;
            var assets = conversionSnapshot == ConversionSnapshot.Valid
                ? Enumerable.Range(0, assetsPerRun)
                    .Select(index => new StorageFixture(
                        Guid.NewGuid(),
                        index,
                        $"parse-runs/{parseRunId:N}/assets/{index:D4}.png"))
                    .ToArray()
                : [];
            var artifacts = conversionSnapshot == ConversionSnapshot.Valid
                ? Enumerable.Range(0, artifactsPerRun)
                    .Select(index => new StorageFixture(
                        index == 0 ? conversionArtifactId : Guid.NewGuid(),
                        index,
                        index == 0
                            ? conversionStorageRef!
                            : $"parse-runs/{parseRunId:N}/artifacts/{index:D4}.bin"))
                    .ToArray()
                : [];
            var segments = conversionSnapshot == ConversionSnapshot.Valid
                ? Enumerable.Range(0, segmentsPerRun)
                    .Select(index => new StorageFixture(
                        Guid.NewGuid(),
                        index,
                        $"parse-runs/{parseRunId:N}/segments/{index:D4}.pdf"))
                    .ToArray()
                : [];
            return new ParseRunFixture(
                parseRunId,
                conversionArtifactId,
                conversionStorageRef,
                assets,
                artifacts,
                segments);
        }

        private sealed record ParseRunFixture(
            Guid Id,
            Guid ConversionArtifactId,
            string? ConversionStorageRef,
            IReadOnlyList<StorageFixture> Assets,
            IReadOnlyList<StorageFixture> Artifacts,
            IReadOnlyList<StorageFixture> Segments);

        private sealed record StorageFixture(Guid Id, int Index, string StorageRef);

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
