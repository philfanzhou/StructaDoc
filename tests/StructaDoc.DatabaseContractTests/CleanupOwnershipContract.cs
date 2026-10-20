using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.CleanupJobs;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Domain.Resources;

namespace StructaDoc.DatabaseContractTests;

/// <summary>
/// Cleanup-job ownership fencing on every supported database. A successful claim advances the
/// row's concurrency version, and only an attempt holding that version may write a retry
/// transition while the job is still running. The scenarios below reproduce, per dialect, what the
/// Worker's exception path does when its claim was reclaimed or the job already finished: the
/// conditional update matches no row, and the newer state survives untouched.
/// </summary>
internal static class CleanupOwnershipContract
{
    public static async Task AssertAsync(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion = null)
    {
        var options = CreateOptions(provider, connectionString, serverVersion);
        await using (var migrate = new StructaDocDbContext(options))
        {
            await migrate.Database.MigrateAsync();
        }

        var nowUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var jobId = Guid.NewGuid();
        var claimDeadlineUtc = nowUtc.AddMinutes(5);
        var failedAtUtc = nowUtc.AddSeconds(30);
        var retryAtUtc = nowUtc.AddSeconds(32);
        var secondClaimDeadlineUtc = nowUtc.AddMinutes(11);

        await using (var seed = new StructaDocDbContext(options))
        {
            // The post-claim state one Worker attempt leaves behind: running under the version its
            // claim wrote, with the reclaim deadline and attempt count of that claim.
            seed.CleanupJobs.Add(new CleanupJobEntity
            {
                Id = jobId,
                TargetType = CleanupTargetTypes.ParseRun,
                TargetId = Guid.NewGuid(),
                StorageRefsJson = "[]",
                Status = CleanupJobStatuses.Running,
                AttemptCount = 1,
                NextAttemptAtUtc = claimDeadlineUtc,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                ConcurrencyVersion = 1,
            });
            await seed.SaveChangesAsync();
        }

        await using (var dbContext = new StructaDocDbContext(options))
        {
            var store = new EfCoreCleanupJobStateStore(dbContext);

            // An owned failure schedules its retry through the conditional update.
            Assert.True(await store.TryScheduleRetryAsync(
                jobId,
                claimConcurrencyVersion: 1,
                errorMessage: "transient storage failure",
                failedAtUtc,
                retryAtUtc));
            var retried = await LoadAsync(dbContext, jobId);
            Assert.Equal(CleanupJobStatuses.RetryWait, retried.Status);
            Assert.Equal("transient storage failure", retried.ErrorMessage);
            Assert.Equal(1, retried.AttemptCount);
            Assert.Equal(retryAtUtc, retried.NextAttemptAtUtc);
            Assert.Equal(failedAtUtc, retried.UpdatedAtUtc);
            Assert.Equal(2, retried.ConcurrencyVersion);

            // The same attempt cannot schedule a second retry after its version already moved.
            Assert.False(await store.TryScheduleRetryAsync(
                jobId,
                claimConcurrencyVersion: 1,
                errorMessage: "duplicate failure",
                failedAtUtc.AddSeconds(1),
                retryAtUtc.AddSeconds(1)));
            retried = await LoadAsync(dbContext, jobId);
            Assert.Equal(CleanupJobStatuses.RetryWait, retried.Status);
            Assert.Equal("transient storage failure", retried.ErrorMessage);
            Assert.Equal(retryAtUtc, retried.NextAttemptAtUtc);
            Assert.Equal(2, retried.ConcurrencyVersion);

            // Another Worker reclaims the job: a new claim version with a new deadline.
            await dbContext.CleanupJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CleanupJobStatuses.Running)
                    .SetProperty(job => job.AttemptCount, 2)
                    .SetProperty(job => job.NextAttemptAtUtc, secondClaimDeadlineUtc)
                    .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1));

            // The stale attempt's failure must not move the running claim to retry-wait.
            Assert.False(await store.TryScheduleRetryAsync(
                jobId,
                claimConcurrencyVersion: 1,
                errorMessage: "late failure",
                failedAtUtc.AddSeconds(2),
                retryAtUtc.AddSeconds(2)));
            var reclaimed = await LoadAsync(dbContext, jobId);
            Assert.Equal(CleanupJobStatuses.Running, reclaimed.Status);
            Assert.Equal(2, reclaimed.AttemptCount);
            Assert.Equal(secondClaimDeadlineUtc, reclaimed.NextAttemptAtUtc);
            Assert.Equal(3, reclaimed.ConcurrencyVersion);

            // The newer claim completes the job.
            var completedAtUtc = nowUtc.AddMinutes(6);
            await dbContext.CleanupJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CleanupJobStatuses.Completed)
                    .SetProperty(job => job.ErrorMessage, (string?)null)
                    .SetProperty(job => job.UpdatedAtUtc, completedAtUtc)
                    .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1));

            // Neither a stale failure nor the completion of an attempt that lost its claim may
            // reopen or edit the finished job.
            Assert.False(await store.TryScheduleRetryAsync(
                jobId,
                claimConcurrencyVersion: 3,
                errorMessage: "late completion",
                failedAtUtc.AddSeconds(3),
                retryAtUtc.AddSeconds(3)));
            var completed = await LoadAsync(dbContext, jobId);
            Assert.Equal(CleanupJobStatuses.Completed, completed.Status);
            Assert.Null(completed.ErrorMessage);
            Assert.Equal(secondClaimDeadlineUtc, completed.NextAttemptAtUtc);
            Assert.Equal(completedAtUtc, completed.UpdatedAtUtc);
            Assert.Equal(4, completed.ConcurrencyVersion);
        }

        await using var cleanup = new StructaDocDbContext(options);
        await cleanup.CleanupJobs.Where(job => job.Id == jobId).ExecuteDeleteAsync();
    }

    private static async Task<CleanupJobEntity> LoadAsync(
        StructaDocDbContext dbContext,
        Guid jobId) => await dbContext.CleanupJobs
        .AsNoTracking()
        .SingleAsync(job => job.Id == jobId);

    private static DbContextOptions<StructaDocDbContext> CreateOptions(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion)
    {
        var databaseOptions = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
            ServerVersion = serverVersion,
            ApplyMigrationsOnStartup = false,
        };
        var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(
            optionsBuilder,
            databaseOptions);
        return optionsBuilder.Options;
    }
}
