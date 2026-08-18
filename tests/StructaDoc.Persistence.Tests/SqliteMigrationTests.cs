using Microsoft.EntityFrameworkCore;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class SqliteMigrationTests
{
    [Fact]
    public async Task Initial_migration_supports_core_records_and_optimistic_concurrency()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var databasePath = Path.Combine(testDirectory, "structadoc.db");
            var options = CreateOptions(databasePath);
            var documentId = Guid.NewGuid();
            var parseRunId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await using (var dbContext = new StructaDocDbContext(options))
            {
                await dbContext.Database.MigrateAsync(
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync(
                    cancellationToken: TestContext.Current.CancellationToken));

                dbContext.Documents.Add(new DocumentEntity
                {
                    Id = documentId,
                    OriginalFileName = "sample.pdf",
                    MediaType = "application/pdf",
                    Extension = ".pdf",
                    SizeBytes = 128,
                    Sha256 = new string('a', 64),
                    StorageRef = "documents/sample.pdf",
                    CreatedAtUtc = now,
                });
                dbContext.ParseRuns.Add(new ParseRunEntity
                {
                    Id = parseRunId,
                    DocumentId = documentId,
                    Status = ParseRunStatuses.Queued,
                    ProviderType = "test-provider",
                    ProviderConfigId = Guid.NewGuid(),
                    ProviderConfigVersion = Guid.NewGuid(),
                    OptionsJson = "{}",
                    SourceMediaType = "application/pdf",
                    SubmittedMediaType = "application/pdf",
                    MaxAttempts = 3,
                    NextAttemptAtUtc = now,
                    CreatedAtUtc = now,
                });

                await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using var firstWorker = new StructaDocDbContext(options);
            await using var secondWorker = new StructaDocDbContext(options);
            var firstClaim = await firstWorker.ParseRuns.SingleAsync(
                run => run.Id == parseRunId,
                cancellationToken: TestContext.Current.CancellationToken);
            var secondClaim = await secondWorker.ParseRuns.SingleAsync(
                run => run.Id == parseRunId,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(DateTimeKind.Utc, firstClaim.NextAttemptAtUtc.Kind);

            firstClaim.Status = ParseRunStatuses.Claimed;
            firstClaim.ClaimedBy = "worker-1";
            firstClaim.LeaseExpiresAtUtc = now.AddMinutes(1);
            await firstWorker.SaveChangesAsync(TestContext.Current.CancellationToken);

            secondClaim.Status = ParseRunStatuses.Claimed;
            secondClaim.ClaimedBy = "worker-2";
            secondClaim.LeaseExpiresAtUtc = now.AddMinutes(1);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => secondWorker.SaveChangesAsync(TestContext.Current.CancellationToken));

            Assert.Equal(1, firstClaim.ConcurrencyVersion);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static DbContextOptions<StructaDocDbContext> CreateOptions(string databasePath)
    {
        return new DbContextOptionsBuilder<StructaDocDbContext>()
            .UseSqlite(
                $"Data Source={databasePath};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(SqliteDesignTimeDbContextFactory).Assembly))
            .Options;
    }
}
