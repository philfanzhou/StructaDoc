using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;
using StructaDoc.Infrastructure.Persistence.ParseRuns;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class SqliteParseRunStateStoreTests
{
    [Fact]
    public async Task Claimed_run_starts_only_with_the_current_lease()
    {
        await using var database = await StateTestDatabase.CreateAsync(maxAttempts: 3);
        var nowUtc = DateTime.UtcNow;
        var claimedLease = await database.ClaimAsync(nowUtc);

        await using var dbContext = new StructaDocDbContext(database.Options);
        var store = new EfCoreParseRunStateStore(dbContext);
        var runningLease = await store.TryStartAsync(
            claimedLease,
            ParseRunStages.Validating,
            nowUtc.AddSeconds(1));
        var staleStart = await store.TryStartAsync(
            claimedLease,
            ParseRunStages.Validating,
            nowUtc.AddSeconds(2));

        Assert.NotNull(runningLease);
        Assert.Equal(claimedLease.ConcurrencyVersion + 1, runningLease.ConcurrencyVersion);
        Assert.Null(staleStart);

        var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync();
        Assert.Equal(ParseRunStatuses.Running, persistedRun.Status);
        Assert.Equal(ParseRunStages.Validating, persistedRun.Stage);
        Assert.NotNull(persistedRun.StartedAtUtc);
        Assert.Equal(2, persistedRun.ConcurrencyVersion);
    }

    [Fact]
    public async Task Stage_and_provider_submission_updates_require_the_current_lease()
    {
        await using var database = await StateTestDatabase.CreateAsync(maxAttempts: 3);
        var nowUtc = DateTime.UtcNow;
        var runningLease = await database.ClaimAndStartAsync(nowUtc);

        await using var dbContext = new StructaDocDbContext(database.Options);
        var store = new EfCoreParseRunStateStore(dbContext);
        var submittingLease = await store.TryUpdateStageAsync(
            runningLease,
            ParseRunStages.Submitting,
            nowUtc.AddSeconds(2));
        Assert.NotNull(submittingLease);

        var staleSubmission = await store.TryRecordProviderSubmissionAsync(
            runningLease,
            "provider-task-stale",
            nowUtc.AddSeconds(3));
        Assert.Null(staleSubmission);

        var submittedLease = await store.TryRecordProviderSubmissionAsync(
            submittingLease,
            "provider-task-1",
            nowUtc.AddSeconds(3));
        Assert.NotNull(submittedLease);

        var downloadingLease = await store.TryUpdateStageAsync(
            submittedLease,
            ParseRunStages.Downloading,
            nowUtc.AddSeconds(4));
        Assert.NotNull(downloadingLease);
        Assert.Null(await store.TryUpdateStageAsync(
            downloadingLease,
            ParseRunStages.Submitting,
            nowUtc.AddSeconds(5)));

        var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync();
        Assert.Equal(ParseRunStatuses.Running, persistedRun.Status);
        Assert.Equal(ParseRunStages.Downloading, persistedRun.Stage);
        Assert.Equal("provider-task-1", persistedRun.ExternalTaskId);
        Assert.Equal(5, persistedRun.ConcurrencyVersion);
    }

    [Fact]
    public async Task Provider_submission_rejects_unsafe_external_task_ids()
    {
        await using var database = await StateTestDatabase.CreateAsync(maxAttempts: 3);
        var nowUtc = DateTime.UtcNow;
        var runningLease = await database.ClaimAndStartAsync(nowUtc);

        await using var dbContext = new StructaDocDbContext(database.Options);
        var store = new EfCoreParseRunStateStore(dbContext);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TryRecordProviderSubmissionAsync(
                runningLease,
                " provider-task ",
                nowUtc.AddSeconds(2)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TryRecordProviderSubmissionAsync(
                runningLease,
                new string('x', 513),
                nowUtc.AddSeconds(2)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TryRecordProviderSubmissionAsync(
                runningLease,
                "provider\ntask",
                nowUtc.AddSeconds(2)));
    }

    [Fact]
    public async Task Retryable_failure_waits_and_returns_to_queue_when_due()
    {
        await using var database = await StateTestDatabase.CreateAsync(maxAttempts: 3);
        var nowUtc = DateTime.UtcNow;
        var runningLease = await database.ClaimAndStartAsync(nowUtc);
        var retryAtUtc = nowUtc.AddMinutes(2);

        await using var dbContext = new StructaDocDbContext(database.Options);
        var store = new EfCoreParseRunStateStore(dbContext);
        var transition = await store.TryRecordFailureAsync(
            runningLease,
            "provider-temporary-error",
            "The provider is temporarily unavailable.",
            retryable: true,
            retryAtUtc,
            nowUtc.AddSeconds(5));
        var queuedEarly = await store.QueueDueRetriesAsync(
            retryAtUtc.AddTicks(-1),
            maxCount: 10);
        var queuedWhenDue = await store.QueueDueRetriesAsync(
            retryAtUtc,
            maxCount: 10);

        Assert.NotNull(transition);
        Assert.Equal(ParseRunStatuses.RetryWait, transition.Status);
        Assert.Equal(0, queuedEarly);
        Assert.Equal(1, queuedWhenDue);

        var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync();
        Assert.Equal(ParseRunStatuses.Queued, persistedRun.Status);
        Assert.Null(persistedRun.ClaimedBy);
        Assert.Null(persistedRun.LeaseExpiresAtUtc);
        Assert.Null(persistedRun.CompletedAtUtc);
        Assert.Equal("provider-temporary-error", persistedRun.ErrorCode);
        Assert.Equal(4, persistedRun.ConcurrencyVersion);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task Permanent_or_exhausted_failure_becomes_final(
        bool retryable,
        int maxAttempts)
    {
        await using var database = await StateTestDatabase.CreateAsync(maxAttempts);
        var nowUtc = DateTime.UtcNow;
        var runningLease = await database.ClaimAndStartAsync(nowUtc);

        await using var dbContext = new StructaDocDbContext(database.Options);
        var store = new EfCoreParseRunStateStore(dbContext);
        var transition = await store.TryRecordFailureAsync(
            runningLease,
            "invalid-input",
            "The input cannot be parsed.",
            retryable,
            nowUtc.AddMinutes(1),
            nowUtc.AddSeconds(5));

        Assert.NotNull(transition);
        Assert.Equal(ParseRunStatuses.Failed, transition.Status);

        var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync();
        Assert.Equal(ParseRunStatuses.Failed, persistedRun.Status);
        Assert.NotNull(persistedRun.CompletedAtUtc);
        Assert.Null(persistedRun.ClaimedBy);
        Assert.Null(persistedRun.LeaseExpiresAtUtc);
    }

    private sealed class StateTestDatabase : IAsyncDisposable
    {
        private StateTestDatabase(
            string directoryPath,
            DbContextOptions<StructaDocDbContext> options)
        {
            DirectoryPath = directoryPath;
            Options = options;
        }

        private string DirectoryPath { get; }

        public DbContextOptions<StructaDocDbContext> Options { get; }

        public static async Task<StateTestDatabase> CreateAsync(int maxAttempts)
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
            var database = new StateTestDatabase(directoryPath, options);
            var nowUtc = DateTime.UtcNow;

            await using var dbContext = new StructaDocDbContext(options);
            await dbContext.Database.MigrateAsync();

            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "state-test.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 128,
                Sha256 = new string('a', 64),
                StorageRef = "documents/state-test.pdf",
                CreatedAtUtc = nowUtc,
            };
            dbContext.Documents.Add(document);
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                Status = ParseRunStatuses.Queued,
                ProviderType = "test-provider",
                ProviderConfigId = Guid.NewGuid(),
                ProviderConfigVersion = Guid.NewGuid(),
                OptionsJson = "{}",
                SourceMediaType = "application/pdf",
                SubmittedMediaType = "application/pdf",
                MaxAttempts = maxAttempts,
                NextAttemptAtUtc = nowUtc.AddMinutes(-1),
                CreatedAtUtc = nowUtc,
            });
            await dbContext.SaveChangesAsync();

            return database;
        }

        public async Task<ParseRunLease> ClaimAsync(DateTime nowUtc)
        {
            await using var dbContext = new StructaDocDbContext(Options);
            var store = new EfCoreParseRunLeaseStore(dbContext);
            var lease = await store.TryClaimNextAsync(
                "state-test-worker",
                nowUtc,
                TimeSpan.FromMinutes(5));
            return Assert.IsType<ParseRunLease>(lease);
        }

        public async Task<ParseRunLease> ClaimAndStartAsync(DateTime nowUtc)
        {
            var claimedLease = await ClaimAsync(nowUtc);
            await using var dbContext = new StructaDocDbContext(Options);
            var store = new EfCoreParseRunStateStore(dbContext);
            var runningLease = await store.TryStartAsync(
                claimedLease,
                ParseRunStages.Validating,
                nowUtc.AddSeconds(1));
            return Assert.IsType<ParseRunLease>(runningLease);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(DirectoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
