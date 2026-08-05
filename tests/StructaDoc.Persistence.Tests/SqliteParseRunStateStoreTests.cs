using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
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
    public async Task Conversion_snapshot_is_saved_atomically_under_the_current_lease()
    {
        const string sourceMediaType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        await using var database = await StateTestDatabase.CreateAsync(
            maxAttempts: 3,
            sourceMediaType,
            ".xlsx");
        var nowUtc = DateTime.UtcNow;
        var runningLease = await database.ClaimAndStartAsync(nowUtc);

        await using var dbContext = new StructaDocDbContext(database.Options);
        var stateStore = new EfCoreParseRunStateStore(dbContext);
        var convertingLease = Assert.IsType<ParseRunLease>(
            await stateStore.TryUpdateStageAsync(
                runningLease,
                ParseRunStages.Converting,
                nowUtc.AddSeconds(2)));
        var conversion = new ParseRunConversion(
            "libreoffice",
            "LibreOffice 25.2.4.2",
            sourceMediaType,
            "application/pdf",
            Guid.NewGuid(),
            "normalized.pdf",
            1024,
            new string('b', 64),
            $"parse-runs/{convertingLease.ParseRunId:N}/conversions/output.pdf",
            "pdf");
        var store = new EfCoreParseRunConversionStore(dbContext);

        var staleSave = await store.TrySaveAsync(
            runningLease,
            conversion,
            nowUtc.AddSeconds(3));
        var savedLease = await store.TrySaveAsync(
            convertingLease,
            conversion,
            nowUtc.AddSeconds(3));

        Assert.Null(staleSave);
        Assert.NotNull(savedLease);
        var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync();
        Assert.Equal(ParseRunStages.PreparingSource, persistedRun.Stage);
        Assert.Equal("application/pdf", persistedRun.SubmittedMediaType);
        Assert.Equal(conversion, ParseRunConversion.FromJson(persistedRun.ConversionJson!));
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Submission_checkpoint_is_retained_only_for_retryable_failures(
        bool retryable,
        bool expectedToRemain)
    {
        await using var database = await StateTestDatabase.CreateAsync(maxAttempts: 3);
        var nowUtc = DateTime.UtcNow;
        var runningLease = await database.ClaimAndStartAsync(nowUtc);
        var checkpoint = new ProviderSubmissionCheckpoint(
            "batch-1",
            "https://upload.example/signed?secret=value");

        await using var dbContext = new StructaDocDbContext(database.Options);
        var stateStore = new EfCoreParseRunStateStore(dbContext);
        var submittingLease = Assert.IsType<ParseRunLease>(
            await stateStore.TryUpdateStageAsync(
                runningLease,
                ParseRunStages.Submitting,
                nowUtc.AddSeconds(2)));
        var checkpointStore = new EfCoreParseRunSubmissionCheckpointStore(
            dbContext,
            new TestSecretProtector());
        var checkpointedLease = Assert.IsType<ParseRunLease>(
            await checkpointStore.TrySaveAsync(
                submittingLease,
                checkpoint,
                nowUtc.AddSeconds(3)));

        var transition = await stateStore.TryRecordFailureAsync(
            checkpointedLease,
            "provider-submit-failed",
            "The submission failed.",
            retryable,
            nowUtc.AddMinutes(1),
            nowUtc.AddSeconds(4));

        Assert.NotNull(transition);
        var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync();
        Assert.Equal(
            expectedToRemain,
            persistedRun.ProtectedSubmissionContinuation is not null);
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

    [Fact]
    public async Task Retried_run_with_external_task_preserves_its_recovery_stage()
    {
        await using var database = await StateTestDatabase.CreateAsync(maxAttempts: 3);
        var nowUtc = DateTime.UtcNow;
        var runningLease = await database.ClaimAndStartAsync(nowUtc);

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseRunStateStore(dbContext);
            var submittingLease = Assert.IsType<ParseRunLease>(
                await store.TryUpdateStageAsync(
                    runningLease,
                    ParseRunStages.Submitting,
                    nowUtc.AddSeconds(2)));
            var submittedLease = Assert.IsType<ParseRunLease>(
                await store.TryRecordProviderSubmissionAsync(
                    submittingLease,
                    "provider-task-1",
                    nowUtc.AddSeconds(3)));
            Assert.NotNull(await store.TryRecordFailureAsync(
                submittedLease,
                "provider-temporary-error",
                "The Provider is temporarily unavailable.",
                retryable: true,
                nowUtc.AddMinutes(1),
                nowUtc.AddSeconds(4)));
            Assert.Equal(1, await store.QueueDueRetriesAsync(
                nowUtc.AddMinutes(1),
                maxCount: 1));
        }

        ParseRunLease claimedRetry;
        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var leaseStore = new EfCoreParseRunLeaseStore(dbContext);
            claimedRetry = Assert.IsType<ParseRunLease>(
                await leaseStore.TryClaimNextAsync(
                    "retry-worker",
                    nowUtc.AddMinutes(1),
                    TimeSpan.FromMinutes(5)));
        }

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseRunStateStore(dbContext);
            Assert.NotNull(await store.TryStartAsync(
                claimedRetry,
                ParseRunStages.Validating,
                nowUtc.AddMinutes(1).AddSeconds(1)));

            var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync();
            Assert.Equal(ParseRunStatuses.Running, persistedRun.Status);
            Assert.Equal(ParseRunStages.WaitingProvider, persistedRun.Stage);
            Assert.Equal("provider-task-1", persistedRun.ExternalTaskId);
            Assert.Equal(2, persistedRun.AttemptCount);
        }
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

        public static async Task<StateTestDatabase> CreateAsync(
            int maxAttempts,
            string sourceMediaType = "application/pdf",
            string sourceExtension = ".pdf")
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
                OriginalFileName = $"state-test{sourceExtension}",
                MediaType = sourceMediaType,
                Extension = sourceExtension,
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
                SourceMediaType = sourceMediaType,
                SubmittedMediaType = sourceMediaType,
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

    private sealed class TestSecretProtector : IProviderSubmissionProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";

        public string Unprotect(string protectedValue) =>
            protectedValue["protected:".Length..];
    }
}
