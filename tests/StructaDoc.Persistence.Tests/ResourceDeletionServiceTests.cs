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
        var expected = new List<string>
        {
            database.AssetStorageRef,
            database.ConversionStorageRef,
            database.ProviderArchiveStorageRef,
            database.SegmentStorageRef,
            database.SegmentProviderArchiveStorageRef,
        };
        if (target == DeletionTarget.Document)
        {
            expected.Add(database.DocumentStorageRef);
        }

        Assert.Equal(expected, refs);
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
            target == DeletionTarget.Document
                ? [database.ProviderArchiveStorageRef, database.DocumentStorageRef]
                : [database.ProviderArchiveStorageRef],
            JsonSerializer.Deserialize<string[]>(refsJson));
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
            Guid parseRunId,
            Guid segmentId)
        {
            Directory = directory;
            Options = options;
            DocumentId = documentId;
            ParseRunId = parseRunId;
            SegmentId = segmentId;
        }

        public const string PrivateStorageRef = "private/conversion/storage-ref.pdf";

        private string Directory { get; }
        private Guid SegmentId { get; }
        public DbContextOptions<StructaDocDbContext> Options { get; }
        public Guid DocumentId { get; }
        public Guid ParseRunId { get; }
        public string DocumentStorageRef => $"documents/{DocumentId:N}/original.pdf";
        public string AssetStorageRef => $"parse-runs/{ParseRunId:N}/assets/image.png";
        public string ConversionStorageRef =>
            $"parse-runs/{ParseRunId:N}/conversions/normalized.pdf";
        public string ProviderArchiveStorageRef =>
            $"parse-runs/{ParseRunId:N}/provider/result.zip";
        public string SegmentStorageRef =>
            $"parse-runs/{ParseRunId:N}/segments/0000.pdf";
        public string SegmentProviderArchiveStorageRef =>
            $"parse-runs/{SegmentId:N}/provider/result.zip";

        public static async Task<DeletionTestDatabase> CreateAsync(
            ConversionSnapshot conversionSnapshot)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "structadoc-deletion-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(directory, "deletion.db")};Pooling=False",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly))
                .Options;
            var database = new DeletionTestDatabase(
                directory,
                options,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid());
            var nowUtc = DateTime.UtcNow;
            var artifactId = Guid.NewGuid();

            await using var dbContext = new StructaDocDbContext(options);
            await dbContext.Database.MigrateAsync();
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
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = database.ParseRunId,
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
                        artifactId,
                        "normalized.pdf",
                        7,
                        PayloadSha256,
                        database.ConversionStorageRef,
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

            if (conversionSnapshot == ConversionSnapshot.Valid)
            {
                dbContext.ParseAssets.Add(new ParseAssetEntity
                {
                    Id = Guid.NewGuid(),
                    ParseRunId = database.ParseRunId,
                    Name = "image.png",
                    MediaType = "image/png",
                    SizeBytes = 7,
                    Sha256 = PayloadSha256,
                    StorageRef = database.AssetStorageRef,
                    CreatedAtUtc = nowUtc,
                });
                dbContext.ParseArtifacts.Add(new ParseArtifactEntity
                {
                    Id = artifactId,
                    ParseRunId = database.ParseRunId,
                    Type = "normalized-pdf",
                    Name = "normalized.pdf",
                    MediaType = "application/pdf",
                    SizeBytes = 7,
                    Sha256 = PayloadSha256,
                    StorageRef = database.ConversionStorageRef,
                    CreatedAtUtc = nowUtc,
                });
                dbContext.ParseSegments.Add(new ParseSegmentEntity
                {
                    Id = database.SegmentId,
                    ParseRunId = database.ParseRunId,
                    Index = 0,
                    StartPage = 1,
                    EndPage = 1,
                    StorageRef = database.SegmentStorageRef,
                    SizeBytes = 7,
                    Sha256 = PayloadSha256,
                    Status = ParseRunStatuses.Succeeded,
                    UpdatedAtUtc = nowUtc,
                });
            }

            await dbContext.SaveChangesAsync();
            return database;
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
