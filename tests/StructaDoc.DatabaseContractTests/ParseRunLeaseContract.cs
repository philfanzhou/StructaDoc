using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
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
            var store = new EfCoreParseRunLeaseStore(dbContext);
            var recoveredCount = await store.RequeueExpiredUnstartedClaimsAsync(
                nowUtc,
                maxCount: ParseRunCount);

            Assert.Equal(1, recoveredCount);
        }

        await using (var dbContext = new StructaDocDbContext(options))
        {
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            var runningLease = await stateStore.TryStartAsync(
                externalTaskLease,
                ParseRunStages.Submitting,
                nowUtc.AddSeconds(1));
            Assert.NotNull(runningLease);
            var checkpoint = new ProviderSubmissionCheckpoint(
                "provider-task-1",
                "https://upload.example/signed?secret=value");
            var checkpointStore = new EfCoreParseRunSubmissionCheckpointStore(
                dbContext,
                new ContractSecretProtector());
            var checkpointedLease = await checkpointStore.TrySaveAsync(
                runningLease,
                checkpoint,
                nowUtc.AddSeconds(2));
            Assert.NotNull(checkpointedLease);
            var submittedLease = await checkpointStore.TryCompleteAsync(
                checkpointedLease,
                checkpoint,
                nowUtc.AddSeconds(3));
            Assert.NotNull(submittedLease);
            var downloadingLease = await stateStore.TryUpdateStageAsync(
                submittedLease,
                ParseRunStages.Downloading,
                nowUtc.AddSeconds(4));
            Assert.NotNull(downloadingLease);

            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == externalTaskLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        parseRun => parseRun.LeaseExpiresAtUtc,
                        nowUtc.AddSeconds(-1)));
        }

        ParseRunLease recoveredRunningLease;
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var leaseStore = new EfCoreParseRunLeaseStore(dbContext);
            recoveredRunningLease = Assert.IsType<ParseRunLease>(
                await leaseStore.TryRecoverNextRunningAsync(
                    "recovery-worker",
                    nowUtc,
                    TimeSpan.FromMinutes(1)));
            Assert.Equal(externalTaskLease.ParseRunId, recoveredRunningLease.ParseRunId);

            var recoveredRun = await dbContext.ParseRuns
                .AsNoTracking()
                .SingleAsync(parseRun => parseRun.Id == recoveredRunningLease.ParseRunId);
            Assert.Equal(ParseRunStages.Downloading, recoveredRun.Stage);
            Assert.Equal("provider-task-1", recoveredRun.ExternalTaskId);
            Assert.Equal(1, recoveredRun.AttemptCount);
        }

        var resultLease = successfulClaims[4]!;
        var resultStorage = new ContractFileStorage();
        var resultFile = resultStorage.Add(
            $"results/{resultLease.ParseRunId:N}/full.md",
            Encoding.UTF8.GetBytes("# Contract result"));
        var bundle = new ParseBundle(
            ParseBundleValidator.CurrentSchemaVersion,
            resultLease.ParseRunId,
            [new ParsePage(1, 1000, 1400, "pixel")],
            [new ParseBlock(Guid.NewGuid(), 0, 1, "text", Content: "Contract result")],
            [],
            [new ParseArtifact(
                Guid.NewGuid(),
                ArtifactTypes.Markdown,
                "full.md",
                "text/markdown",
                resultFile.SizeBytes,
                resultFile.Sha256,
                resultFile.StorageRef)],
            "{\"providerType\":\"test-provider\"}");
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            var runningLease = await stateStore.TryStartAsync(
                resultLease,
                ParseRunStages.Persisting,
                nowUtc.AddSeconds(1));
            Assert.NotNull(runningLease);

            var resultStore = new EfCoreParseBundleCommitStore(dbContext, resultStorage);
            var commit = await resultStore.TryCommitAsync(
                runningLease,
                bundle,
                nowUtc.AddSeconds(2));
            Assert.Equal(ParseBundleCommitStatus.Committed, commit.Status);
            Assert.Equal(
                ParseBundleCommitStatus.AlreadyCommitted,
                (await resultStore.TryCommitAsync(
                    runningLease,
                    bundle,
                    nowUtc.AddSeconds(3))).Status);
        }

        await using var verificationContext = new StructaDocDbContext(options);
        Assert.Empty(await verificationContext.Database.GetPendingMigrationsAsync());

        var persistedRuns = await verificationContext.ParseRuns
            .AsNoTracking()
            .ToListAsync();
        Assert.Equal(ParseRunCount, persistedRuns.Count);
        Assert.Equal(
            ParseRunCount - 4,
            persistedRuns.Count(parseRun => parseRun.Status == ParseRunStatuses.Claimed));
        Assert.Equal(2, persistedRuns.Count(parseRun =>
            parseRun.Status == ParseRunStatuses.Queued
            && parseRun.ClaimedBy == null
            && parseRun.LeaseExpiresAtUtc == null));
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Running
            && parseRun.ExternalTaskId == "provider-task-1"
            && parseRun.Stage == ParseRunStages.Downloading
            && parseRun.AttemptCount == 1
            && parseRun.ClaimedBy == recoveredRunningLease.WorkerId);
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Succeeded
            && parseRun.ResultSchemaVersion == ParseBundleValidator.CurrentSchemaVersion
            && parseRun.ResultSha256 == ParseBundleValidator.ComputeFingerprint(bundle));
        Assert.Single(await verificationContext.ParsePages.ToListAsync());
        Assert.Single(await verificationContext.ParseBlocks.ToListAsync());
        Assert.Single(await verificationContext.ParseArtifacts.ToListAsync());

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

    private sealed class ContractSecretProtector : IProviderSubmissionProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";

        public string Unprotect(string protectedValue) =>
            protectedValue["protected:".Length..];
    }

    private sealed class ContractFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        public StoredFile Add(string storageRef, byte[] content)
        {
            files.Add(storageRef, content);
            return new StoredFile(
                storageRef,
                content.LongLength,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        }

        public Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream stream = new MemoryStream(files[storageRef], writable: false);
            return Task.FromResult(stream);
        }

        public Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
