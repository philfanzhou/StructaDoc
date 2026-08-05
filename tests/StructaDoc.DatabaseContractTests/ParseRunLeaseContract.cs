using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;
using StructaDoc.Infrastructure.Persistence.ParseRuns;

namespace StructaDoc.DatabaseContractTests;

internal static class ParseRunLeaseContract
{
    private const int ParseRunCount = 12;

    public static async Task AssertAsync(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion = null)
    {
        var options = CreateOptions(provider, connectionString, serverVersion);
        await InitializeAsync(options);

        var nowUtc = DateTime.UtcNow;
        var claims = await Task.WhenAll(
            Enumerable.Range(1, ParseRunCount + 8)
                .Select(workerNumber => ClaimOneAsync(
                    options,
                    $"worker-{workerNumber}",
                    nowUtc)));
        var successfulClaims = claims.Where(claim => claim is not null).ToArray();

        Assert.Equal(ParseRunCount, successfulClaims.Length);
        Assert.Equal(
            ParseRunCount,
            successfulClaims.Select(claim => claim!.ParseRunId).Distinct().Count());

        var originalLease = successfulClaims[0]!;
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var store = new EfCoreParseRunLeaseStore(dbContext);
            var renewedLease = await store.TryRenewLeaseAsync(
                originalLease,
                nowUtc.AddSeconds(10),
                TimeSpan.FromMinutes(1));
            var staleRenewal = await store.TryRenewLeaseAsync(
                originalLease,
                nowUtc.AddSeconds(20),
                TimeSpan.FromMinutes(1));

            Assert.NotNull(renewedLease);
            Assert.Equal(originalLease.ConcurrencyVersion + 1, renewedLease.ConcurrencyVersion);
            Assert.Null(staleRenewal);
        }

        var expiringLease = successfulClaims[1]!;
        var externalTaskLease = successfulClaims[2]!;
        await using (var dbContext = new StructaDocDbContext(options))
        {
            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == expiringLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        parseRun => parseRun.LeaseExpiresAtUtc,
                        nowUtc.AddSeconds(-1)));
            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == externalTaskLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(parseRun => parseRun.ExternalTaskId, "provider-task-1")
                    .SetProperty(
                        parseRun => parseRun.LeaseExpiresAtUtc,
                        nowUtc.AddSeconds(-1)));

            var store = new EfCoreParseRunLeaseStore(dbContext);
            var recoveredCount = await store.RequeueExpiredUnstartedClaimsAsync(
                nowUtc,
                maxCount: ParseRunCount);

            Assert.Equal(1, recoveredCount);
        }

        await using var verificationContext = new StructaDocDbContext(options);
        Assert.Empty(await verificationContext.Database.GetPendingMigrationsAsync());

        var persistedRuns = await verificationContext.ParseRuns
            .AsNoTracking()
            .ToListAsync();
        Assert.Equal(ParseRunCount, persistedRuns.Count);
        Assert.Equal(
            ParseRunCount - 1,
            persistedRuns.Count(parseRun => parseRun.Status == ParseRunStatuses.Claimed));
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Queued
            && parseRun.ClaimedBy == null
            && parseRun.LeaseExpiresAtUtc == null);
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Claimed
            && parseRun.ExternalTaskId == "provider-task-1"
            && parseRun.ClaimedBy != null);
    }

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

    private static async Task InitializeAsync(
        DbContextOptions<StructaDocDbContext> options)
    {
        await using var dbContext = new StructaDocDbContext(options);
        await dbContext.Database.MigrateAsync();

        var nowUtc = DateTime.UtcNow;
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "contract-test.pdf",
            MediaType = "application/pdf",
            Extension = ".pdf",
            SizeBytes = 128,
            Sha256 = new string('a', 64),
            StorageRef = "documents/contract-test.pdf",
            CreatedAtUtc = nowUtc,
        };
        dbContext.Documents.Add(document);

        for (var index = 0; index < ParseRunCount; index++)
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
    }

    private static async Task<ParseRunLease?> ClaimOneAsync(
        DbContextOptions<StructaDocDbContext> options,
        string workerId,
        DateTime nowUtc)
    {
        await using var dbContext = new StructaDocDbContext(options);
        var store = new EfCoreParseRunLeaseStore(dbContext);
        return await store.TryClaimNextAsync(
            workerId,
            nowUtc,
            TimeSpan.FromMinutes(1));
    }
}
