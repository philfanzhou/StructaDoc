using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class SqliteParseRunLeaseStoreTests
{
    [Fact]
    public async Task Concurrent_workers_claim_each_due_run_only_once()
    {
        await using var database = await SqliteLeaseTestDatabase.CreateAsync(parseRunCount: 16);
        var nowUtc = DateTime.UtcNow;

        var claims = await Task.WhenAll(
            Enumerable.Range(1, 24)
                .Select(workerNumber => ClaimOneAsync(
                    database.Options,
                    $"worker-{workerNumber}",
                    nowUtc)));
        var successfulClaims = claims.Where(claim => claim is not null).ToArray();

        Assert.Equal(16, successfulClaims.Length);
        Assert.Equal(
            successfulClaims.Length,
            successfulClaims.Select(claim => claim!.ParseRunId).Distinct().Count());

        await using var verificationContext = new StructaDocDbContext(database.Options);
        var persistedRuns = await verificationContext.ParseRuns
            .AsNoTracking()
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(persistedRuns, parseRun =>
        {
            Assert.Equal(ParseRunStatuses.Claimed, parseRun.Status);
            Assert.NotNull(parseRun.ClaimedBy);
            Assert.NotNull(parseRun.LeaseExpiresAtUtc);
            Assert.Equal(1, parseRun.AttemptCount);
            Assert.Equal(1, parseRun.ConcurrencyVersion);
        });
    }

    [Fact]
    public async Task Renewing_a_lease_invalidates_the_previous_lease_token()
    {
        await using var database = await SqliteLeaseTestDatabase.CreateAsync(parseRunCount: 1);
        var nowUtc = DateTime.UtcNow;
        var originalLease = await ClaimOneAsync(database.Options, "worker-1", nowUtc);
        Assert.NotNull(originalLease);

        await using var dbContext = new StructaDocDbContext(database.Options);
        var store = new EfCoreParseRunLeaseStore(dbContext);
        var renewedLease = await store.TryRenewLeaseAsync(
            originalLease,
            nowUtc.AddSeconds(10),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        var staleRenewal = await store.TryRenewLeaseAsync(
            originalLease,
            nowUtc.AddSeconds(20),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(renewedLease);
        Assert.Equal(originalLease.ConcurrencyVersion + 1, renewedLease.ConcurrencyVersion);
        Assert.Null(staleRenewal);
    }

    [Fact]
    public async Task Expired_claim_without_external_task_is_requeued()
    {
        await using var database = await SqliteLeaseTestDatabase.CreateAsync(parseRunCount: 1);
        var nowUtc = DateTime.UtcNow;
        var lease = await ClaimOneAsync(
            database.Options,
            "worker-1",
            nowUtc,
            TimeSpan.FromSeconds(1));
        Assert.NotNull(lease);

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var store = new EfCoreParseRunLeaseStore(dbContext);
            var recoveredCount = await store.RequeueExpiredUnstartedClaimsAsync(
                nowUtc.AddSeconds(2),
                maxCount: 10,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, recoveredCount);
        }

        await using var verificationContext = new StructaDocDbContext(database.Options);
        var recoveredRun = await verificationContext.ParseRuns.AsNoTracking().SingleAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParseRunStatuses.Queued, recoveredRun.Status);
        Assert.Null(recoveredRun.ClaimedBy);
        Assert.Null(recoveredRun.LeaseExpiresAtUtc);
        Assert.Equal(2, recoveredRun.ConcurrencyVersion);
    }

    [Fact]
    public async Task Expired_claim_with_external_task_is_not_requeued()
    {
        await using var database = await SqliteLeaseTestDatabase.CreateAsync(parseRunCount: 1);
        var nowUtc = DateTime.UtcNow;
        var lease = await ClaimOneAsync(database.Options, "worker-1", nowUtc);
        Assert.NotNull(lease);

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            await dbContext.ParseRuns.ExecuteUpdateAsync(setters => setters
                .SetProperty(parseRun => parseRun.ExternalTaskId, "provider-task-1")
                .SetProperty(
                    parseRun => parseRun.LeaseExpiresAtUtc,
                    nowUtc.AddSeconds(-1)),
            cancellationToken: TestContext.Current.CancellationToken);

            var store = new EfCoreParseRunLeaseStore(dbContext);
            var recoveredCount = await store.RequeueExpiredUnstartedClaimsAsync(
                nowUtc,
                maxCount: 10,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, recoveredCount);
        }

        await using var verificationContext = new StructaDocDbContext(database.Options);
        var persistedRun = await verificationContext.ParseRuns.AsNoTracking().SingleAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParseRunStatuses.Claimed, persistedRun.Status);
        Assert.Equal("provider-task-1", persistedRun.ExternalTaskId);
        Assert.Equal("worker-1", persistedRun.ClaimedBy);
    }

    [Fact]
    public async Task Expired_running_task_is_recovered_once_without_incrementing_attempt()
    {
        await using var database = await SqliteLeaseTestDatabase.CreateAsync(parseRunCount: 1);
        var nowUtc = DateTime.UtcNow;
        var claimedLease = await ClaimOneAsync(database.Options, "worker-1", nowUtc);
        Assert.NotNull(claimedLease);

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            var runningLease = await stateStore.TryStartAsync(
                claimedLease,
                ParseRunStages.Submitting,
                nowUtc.AddSeconds(1),
                TestContext.Current.CancellationToken);
            Assert.NotNull(runningLease);
            var submittedLease = await stateStore.TryRecordProviderSubmissionAsync(
                runningLease,
                "provider-task-1",
                nowUtc.AddSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.NotNull(submittedLease);

            await dbContext.ParseRuns.ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    parseRun => parseRun.LeaseExpiresAtUtc,
                    nowUtc.AddSeconds(-1)),
            cancellationToken: TestContext.Current.CancellationToken);
        }

        var recoveries = await Task.WhenAll(
            Enumerable.Range(1, 8).Select(async workerNumber =>
            {
                await using var dbContext = new StructaDocDbContext(database.Options);
                var leaseStore = new EfCoreParseRunLeaseStore(dbContext);
                return await leaseStore.TryRecoverNextRunningAsync(
                    $"recovery-worker-{workerNumber}",
                    nowUtc,
                    TimeSpan.FromMinutes(1));
            }));

        var recoveredLease = Assert.Single(recoveries, lease => lease is not null);
        Assert.NotNull(recoveredLease);

        await using var verificationContext = new StructaDocDbContext(database.Options);
        var persistedRun = await verificationContext.ParseRuns.AsNoTracking().SingleAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParseRunStatuses.Running, persistedRun.Status);
        Assert.Equal(ParseRunStages.WaitingProvider, persistedRun.Stage);
        Assert.Equal("provider-task-1", persistedRun.ExternalTaskId);
        Assert.Equal(recoveredLease.WorkerId, persistedRun.ClaimedBy);
        Assert.Equal(1, persistedRun.AttemptCount);
        Assert.Equal(4, persistedRun.ConcurrencyVersion);
    }

    [Theory]
    [InlineData(ParseRunStages.Validating, ParseRunStatuses.Queued, null)]
    [InlineData(
        ParseRunStages.Submitting,
        ParseRunStatuses.Failed,
        "provider-submission-outcome-unknown")]
    public async Task Expired_running_task_without_external_id_is_recovered_conservatively(
        string stage,
        string expectedStatus,
        string? expectedErrorCode)
    {
        await using var database = await SqliteLeaseTestDatabase.CreateAsync(parseRunCount: 1);
        var nowUtc = DateTime.UtcNow;
        var claimedLease = await ClaimOneAsync(
            database.Options,
            "worker-1",
            nowUtc,
            TimeSpan.FromSeconds(1));
        Assert.NotNull(claimedLease);

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            Assert.NotNull(await stateStore.TryStartAsync(
                claimedLease,
                stage,
                nowUtc.AddMilliseconds(100),
                TestContext.Current.CancellationToken));
        }

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var leaseStore = new EfCoreParseRunLeaseStore(dbContext);
            var recovery = await leaseStore.RecoverExpiredUnsubmittedRunsAsync(
                nowUtc.AddSeconds(2),
                maxCount: 10,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expectedStatus == ParseRunStatuses.Queued ? 1 : 0, recovery.RequeuedCount);
            Assert.Equal(expectedStatus == ParseRunStatuses.Failed ? 1 : 0, recovery.FailedUnknownSubmissionCount);
        }

        await using var verificationContext = new StructaDocDbContext(database.Options);
        var persistedRun = await verificationContext.ParseRuns.AsNoTracking().SingleAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, persistedRun.Status);
        Assert.Equal(expectedErrorCode, persistedRun.ErrorCode);
        Assert.Null(persistedRun.ClaimedBy);
        Assert.Null(persistedRun.LeaseExpiresAtUtc);
        Assert.Equal(
            expectedStatus == ParseRunStatuses.Failed,
            persistedRun.CompletedAtUtc.HasValue);
    }

    [Fact]
    public async Task Expired_segmented_persisting_run_is_requeued_with_normalized_checkpoints()
    {
        await using var database = await SqliteLeaseTestDatabase.CreateAsync(parseRunCount: 1);
        var nowUtc = DateTime.UtcNow;
        var claimedLease = Assert.IsType<ParseRunLease>(await ClaimOneAsync(
            database.Options,
            "segment-worker",
            nowUtc,
            TimeSpan.FromSeconds(5)));

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            var segmentingLease = Assert.IsType<ParseRunLease>(await stateStore.TryStartAsync(
                claimedLease,
                ParseRunStages.Segmenting,
                nowUtc.AddSeconds(1),
                TestContext.Current.CancellationToken));
            dbContext.ParseSegments.Add(new ParseSegmentEntity
            {
                Id = Guid.NewGuid(),
                ParseRunId = segmentingLease.ParseRunId,
                Index = 0,
                StartPage = 1,
                EndPage = 2,
                StorageRef = $"parse-runs/{segmentingLease.ParseRunId:N}/segments/0000.pdf",
                SizeBytes = 128,
                Sha256 = new string('b', 64),
                Status = "normalized",
                ExternalTaskId = "segment-task-1",
                UpdatedAtUtc = nowUtc.AddSeconds(1),
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(await stateStore.TryUpdateStageAsync(
                segmentingLease,
                ParseRunStages.Persisting,
                nowUtc.AddSeconds(2),
                TestContext.Current.CancellationToken));
            await dbContext.ParseRuns.ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    parseRun => parseRun.LeaseExpiresAtUtc,
                    nowUtc.AddSeconds(3)),
                TestContext.Current.CancellationToken);
        }

        await using (var dbContext = new StructaDocDbContext(database.Options))
        {
            var recovery = await new EfCoreParseRunLeaseStore(dbContext)
                .RecoverExpiredUnsubmittedRunsAsync(
                    nowUtc.AddSeconds(4),
                    maxCount: 10,
                    cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, recovery.RequeuedCount);
            Assert.Equal(0, recovery.FailedUnknownSubmissionCount);
        }

        await using var verificationContext = new StructaDocDbContext(database.Options);
        var persistedRun = await verificationContext.ParseRuns.AsNoTracking().SingleAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParseRunStatuses.Queued, persistedRun.Status);
        Assert.Equal(ParseRunStages.Persisting, persistedRun.Stage);
        Assert.Null(persistedRun.ExternalTaskId);
        var persistedSegment = await verificationContext.ParseSegments
            .AsNoTracking()
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("normalized", persistedSegment.Status);
        Assert.Equal("segment-task-1", persistedSegment.ExternalTaskId);
    }

    private static async Task<StructaDoc.Application.ParseRuns.ParseRunLease?> ClaimOneAsync(
        DbContextOptions<StructaDocDbContext> options,
        string workerId,
        DateTime nowUtc,
        TimeSpan? leaseDuration = null)
    {
        await using var dbContext = new StructaDocDbContext(options);
        var store = new EfCoreParseRunLeaseStore(dbContext);
        return await store.TryClaimNextAsync(
            workerId,
            nowUtc,
            leaseDuration ?? TimeSpan.FromMinutes(1));
    }

    private sealed class SqliteLeaseTestDatabase : IAsyncDisposable
    {
        private SqliteLeaseTestDatabase(
            string directoryPath,
            DbContextOptions<StructaDocDbContext> options)
        {
            DirectoryPath = directoryPath;
            Options = options;
        }

        public string DirectoryPath { get; }

        public DbContextOptions<StructaDocDbContext> Options { get; }

        public static async Task<SqliteLeaseTestDatabase> CreateAsync(int parseRunCount)
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "structadoc-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);

            var databasePath = Path.Combine(directoryPath, "structadoc.db");
            var options = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={databasePath};Pooling=False;Default Timeout=30",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly))
                .Options;
            var database = new SqliteLeaseTestDatabase(directoryPath, options);

            await using var dbContext = new StructaDocDbContext(options);
            await dbContext.Database.MigrateAsync();

            var nowUtc = DateTime.UtcNow;
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "sample.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 128,
                Sha256 = new string('a', 64),
                StorageRef = "documents/sample.pdf",
                CreatedAtUtc = nowUtc,
            };
            dbContext.Documents.Add(document);

            for (var index = 0; index < parseRunCount; index++)
            {
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
                    MaxAttempts = 3,
                    NextAttemptAtUtc = nowUtc.AddMinutes(-1),
                    CreatedAtUtc = nowUtc.AddMilliseconds(index),
                });
            }

            await dbContext.SaveChangesAsync();
            return database;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(DirectoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
