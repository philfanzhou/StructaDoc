using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Infrastructure.Authentication;
using StructaDoc.Infrastructure.Documents;
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

        var retryLease = successfulClaims[3]!;
        var retryAtUtc = nowUtc.AddMinutes(2);
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var store = new EfCoreParseRunStateStore(dbContext);
            var runningLease = await store.TryStartAsync(
                retryLease,
                ParseRunStages.Validating,
                nowUtc.AddSeconds(1));
            Assert.NotNull(runningLease);

            var failure = await store.TryRecordFailureAsync(
                runningLease,
                "provider-temporary-error",
                "The provider is temporarily unavailable.",
                retryable: true,
                retryAtUtc,
                nowUtc.AddSeconds(5));
            Assert.NotNull(failure);
            Assert.Equal(ParseRunStatuses.RetryWait, failure.Status);
            Assert.Equal(0, await store.QueueDueRetriesAsync(
                retryAtUtc.AddTicks(-1),
                maxCount: ParseRunCount));
            Assert.Equal(1, await store.QueueDueRetriesAsync(
                retryAtUtc,
                maxCount: ParseRunCount));
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
            ParseRunCount - 2,
            persistedRuns.Count(parseRun => parseRun.Status == ParseRunStatuses.Claimed));
        Assert.Equal(2, persistedRuns.Count(parseRun =>
            parseRun.Status == ParseRunStatuses.Queued
            && parseRun.ClaimedBy == null
            && parseRun.LeaseExpiresAtUtc == null));
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Claimed
            && parseRun.ExternalTaskId == "provider-task-1"
            && parseRun.ClaimedBy != null);

        var administrator = await verificationContext.AdminUsers
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal("ADMIN@STRUCTADOC.TEST", administrator.NormalizedEmail);

        var apiClient = await verificationContext.ApiClients
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(32, apiClient.SecretHash.Length);
        Assert.Equal("documents:read", apiClient.Scopes);
        Assert.False(apiClient.IsActive);
        Assert.NotNull(apiClient.RevokedAtUtc);
        Assert.Equal(3, apiClient.ConcurrencyVersion);
        Assert.Equal(4, await verificationContext.Documents.CountAsync());
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

        for (var index = 1; index <= 3; index++)
        {
            dbContext.Documents.Add(new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = $"contract-page-{index}.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 128 + index,
                Sha256 = index.ToString().PadLeft(64, 'a'),
                StorageRef = $"documents/contract-page-{index}.pdf",
                CreatedAtUtc = index <= 2 ? nowUtc : nowUtc.AddSeconds(-1),
            });
        }
        dbContext.AdminUsers.Add(new AdminUserEntity
        {
            Id = Guid.NewGuid(),
            Email = "admin@structadoc.test",
            NormalizedEmail = "ADMIN@STRUCTADOC.TEST",
            DisplayName = "Contract administrator",
            PasswordHash = "contract-test-password-hash",
            IsActive = true,
            SecurityStamp = Guid.NewGuid(),
            CreatedAtUtc = nowUtc,
        });
        dbContext.ApiClients.Add(new ApiClientEntity
        {
            Id = Guid.NewGuid(),
            Name = "Contract client",
            SecretHash = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            Scopes = "documents:write",
            IsActive = true,
            CreatedAtUtc = nowUtc,
        });

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

        await ExerciseDocumentReadsAsync(options);
        await ExerciseApiClientAdministrationAsync(options, nowUtc);
    }

    private static async Task ExerciseDocumentReadsAsync(
        DbContextOptions<StructaDocDbContext> options)
    {
        await using var dbContext = new StructaDocDbContext(options);
        var service = new EfCoreDocumentReadService(
            dbContext,
            new UnusedFileStorage(),
            NullLogger<EfCoreDocumentReadService>.Instance);
        var firstPage = await service.ListAsync(limit: 2);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await service.ListAsync(limit: 2, firstPage.NextCursor);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Null(secondPage.NextCursor);
        Assert.Equal(
            4,
            firstPage.Items.Concat(secondPage.Items).Select(item => item.Id).Distinct().Count());
        Assert.NotNull(await service.GetAsync(firstPage.Items[0].Id));
    }

    private static async Task ExerciseApiClientAdministrationAsync(
        DbContextOptions<StructaDocDbContext> options,
        DateTime nowUtc)
    {
        await using var dbContext = new StructaDocDbContext(options);
        var service = new ApiClientAdministrationService(dbContext);
        var existing = Assert.Single(await service.ListAsync());
        Assert.True(ApiClientDefinition.TryCreate(
            "Updated contract client",
            [AuthenticationScopes.DocumentsRead],
            out var definition,
            out _,
            out _));

        var update = await service.UpdateAsync(existing.Id, definition!);
        Assert.Equal(ApiClientMutationStatus.Succeeded, update.Status);
        Assert.Equal([AuthenticationScopes.DocumentsRead], update.Client!.Scopes);

        var rotation = await service.RotateCredentialAsync(existing.Id);
        Assert.Equal(ApiClientMutationStatus.Succeeded, rotation.Status);
        Assert.NotNull(rotation.IssuedClient);
        Assert.StartsWith("sd1.", rotation.IssuedClient.Credential, StringComparison.Ordinal);

        Assert.Equal(
            ApiClientMutationStatus.Succeeded,
            await service.RevokeAsync(existing.Id, nowUtc.AddMinutes(1)));
        Assert.Equal(
            ApiClientMutationStatus.Conflict,
            (await service.RotateCredentialAsync(existing.Id)).Status);
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

    private sealed class UnusedFileStorage : IFileStorage
    {
        public Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
