using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Documents;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;

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
        await AssertCreationGuardsAsync(options);
        await AssertSegmentMutationFencingAsync(options);

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
                retryAtUtc.AddMilliseconds(-1),
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

        var safeUnsubmittedLease = successfulClaims[5]!;
        var unknownSubmissionLease = successfulClaims[6]!;
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            Assert.NotNull(await stateStore.TryStartAsync(
                safeUnsubmittedLease,
                ParseRunStages.Validating,
                nowUtc.AddSeconds(1)));
            Assert.NotNull(await stateStore.TryStartAsync(
                unknownSubmissionLease,
                ParseRunStages.Submitting,
                nowUtc.AddSeconds(1)));
            await dbContext.ParseRuns
                .Where(parseRun =>
                    parseRun.Id == safeUnsubmittedLease.ParseRunId
                    || parseRun.Id == unknownSubmissionLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    parseRun => parseRun.LeaseExpiresAtUtc,
                    nowUtc.AddSeconds(-1)));

            var recovery = await new EfCoreParseRunLeaseStore(dbContext)
                .RecoverExpiredUnsubmittedRunsAsync(
                    nowUtc,
                    maxCount: ParseRunCount);
            Assert.Equal(1, recovery.RequeuedCount);
            Assert.Equal(1, recovery.FailedUnknownSubmissionCount);
        }

        var resultLease = successfulClaims[4]!;
        var resultStorage = new ContractFileStorage();
        var resultFile = resultStorage.Add(
            $"results/{resultLease.ParseRunId:N}/full.md",
            Encoding.UTF8.GetBytes("# Contract result"));
        var convertedFile = resultStorage.Add(
            $"parse-runs/{resultLease.ParseRunId:N}/conversions/normalized.pdf",
            "%PDF-1.7\ncontract-conversion"u8.ToArray());
        var conversion = new ParseRunConversion(
            "libreoffice",
            "LibreOffice contract-version",
            "application/pdf",
            "application/pdf",
            Guid.NewGuid(),
            "normalized.pdf",
            convertedFile.SizeBytes,
            convertedFile.Sha256,
            convertedFile.StorageRef,
            "pdf");
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
                resultFile.StorageRef),
             conversion.ToArtifact()],
            "{\"providerType\":\"test-provider\"}");
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            var runningLease = await stateStore.TryStartAsync(
                resultLease,
                ParseRunStages.Converting,
                nowUtc.AddSeconds(1));
            Assert.NotNull(runningLease);
            var convertedLease = await new EfCoreParseRunConversionStore(dbContext).TrySaveAsync(
                runningLease,
                conversion,
                nowUtc.AddSeconds(2));
            Assert.NotNull(convertedLease);
            var submittingLease = await stateStore.TryUpdateStageAsync(
                convertedLease,
                ParseRunStages.Submitting,
                nowUtc.AddSeconds(3));
            Assert.NotNull(submittingLease);
            var submittedLease = await stateStore.TryRecordProviderSubmissionAsync(
                submittingLease,
                "result-task-1",
                nowUtc.AddSeconds(4));
            Assert.NotNull(submittedLease);
            var persistingLease = await stateStore.TryUpdateStageAsync(
                submittedLease,
                ParseRunStages.Persisting,
                nowUtc.AddSeconds(5));
            Assert.NotNull(persistingLease);

            var resultStore = new EfCoreParseBundleCommitStore(dbContext, resultStorage);
            var commit = await resultStore.TryCommitAsync(
                persistingLease,
                bundle,
                nowUtc.AddSeconds(6));
            Assert.Equal(ParseBundleCommitStatus.Committed, commit.Status);
            Assert.Equal(
                ParseBundleCommitStatus.AlreadyCommitted,
                (await resultStore.TryCommitAsync(
                    persistingLease,
                    bundle,
                    nowUtc.AddSeconds(7))).Status);
        }

        var ownedCancellationLease = successfulClaims[7]!;
        var abandonedCancellationLease = successfulClaims[8]!;
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var service = new EfCoreParseRunService(dbContext);
            var stateStore = new EfCoreParseRunStateStore(dbContext);
            var leaseStore = new EfCoreParseRunLeaseStore(dbContext);

            // A final run is never reopened.
            var finalResult = await service.RequestCancellationAsync(
                resultLease.ParseRunId,
                nowUtc.AddSeconds(8));
            Assert.Equal(ParseRunCancellationStatus.AlreadyFinal, finalResult.Status);
            Assert.Equal(ParseRunStatuses.Succeeded, finalResult.ParseRun!.Status);

            var requested = await service.RequestCancellationAsync(
                ownedCancellationLease.ParseRunId,
                nowUtc.AddSeconds(9));
            Assert.Equal(ParseRunCancellationStatus.Requested, requested.Status);
            Assert.Equal(ParseRunStatuses.CancelRequested, requested.ParseRun!.Status);
            Assert.Equal(
                ParseRunCancellationStatus.AlreadyRequested,
                (await service.RequestCancellationAsync(
                    ownedCancellationLease.ParseRunId,
                    nowUtc.AddSeconds(10))).Status);

            // The request must stop lease renewal so the executing Worker cannot continue.
            Assert.Null(await leaseStore.TryRenewLeaseAsync(
                ownedCancellationLease,
                nowUtc.AddSeconds(11),
                TimeSpan.FromMinutes(1)));

            // A live lease belongs to its Worker, so only that Worker completes it early.
            Assert.Equal(0, await stateStore.FinalizeAbandonedCancellationsAsync(
                nowUtc.AddSeconds(12),
                maxCount: ParseRunCount));
            Assert.False(await stateStore.TryFinalizeOwnedCancellationAsync(
                ownedCancellationLease.ParseRunId,
                "other-worker",
                nowUtc.AddSeconds(13)));
            Assert.True(await stateStore.TryFinalizeOwnedCancellationAsync(
                ownedCancellationLease.ParseRunId,
                ownedCancellationLease.WorkerId,
                nowUtc.AddSeconds(14)));

            Assert.Equal(
                ParseRunCancellationStatus.Requested,
                (await service.RequestCancellationAsync(
                    abandonedCancellationLease.ParseRunId,
                    nowUtc.AddSeconds(15))).Status);
            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == abandonedCancellationLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        parseRun => parseRun.LeaseExpiresAtUtc,
                        nowUtc.AddSeconds(-1)));
            Assert.Equal(1, await stateStore.FinalizeAbandonedCancellationsAsync(
                nowUtc.AddSeconds(16),
                maxCount: ParseRunCount));
        }

        await using var verificationContext = new StructaDocDbContext(options);
        Assert.Empty(await verificationContext.Database.GetPendingMigrationsAsync());

        var persistedRuns = await verificationContext.ParseRuns
            .AsNoTracking()
            .ToListAsync();
        Assert.Equal(ParseRunCount, persistedRuns.Count);
        Assert.Equal(
            ParseRunCount - 8,
            persistedRuns.Count(parseRun => parseRun.Status == ParseRunStatuses.Claimed));
        Assert.Equal(2, persistedRuns.Count(parseRun =>
            parseRun.Status == ParseRunStatuses.Cancelled
            && parseRun.Stage == null
            && parseRun.ClaimedBy == null
            && parseRun.LeaseExpiresAtUtc == null
            && parseRun.ProtectedSubmissionContinuation == null
            && parseRun.CompletedAtUtc != null));
        Assert.Equal(3, persistedRuns.Count(parseRun =>
            parseRun.Status == ParseRunStatuses.Queued
            && parseRun.ClaimedBy == null
            && parseRun.LeaseExpiresAtUtc == null));
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Failed
            && parseRun.ErrorCode == "provider-submission-outcome-unknown"
            && parseRun.CompletedAtUtc != null);
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Running
            && parseRun.ExternalTaskId == "provider-task-1"
            && parseRun.Stage == ParseRunStages.Downloading
            && parseRun.AttemptCount == 1
            && parseRun.ClaimedBy == recoveredRunningLease.WorkerId);
        Assert.Single(persistedRuns, parseRun =>
            parseRun.Status == ParseRunStatuses.Succeeded
            && parseRun.ConversionJson == conversion.ToJson()
            && parseRun.ResultSchemaVersion == ParseBundleValidator.CurrentSchemaVersion
            && parseRun.ResultSha256 == ParseBundleValidator.ComputeFingerprint(bundle));
        Assert.Single(await verificationContext.ParsePages.ToListAsync());
        Assert.Single(await verificationContext.ParseBlocks.ToListAsync());
        Assert.Equal(2, await verificationContext.ParseArtifacts.CountAsync());

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

    private static async Task AssertCreationGuardsAsync(
        DbContextOptions<StructaDocDbContext> options)
    {
        var nowUtc = DateTime.UtcNow;
        var documentId = Guid.NewGuid();
        var configId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await using (var seed = new StructaDocDbContext(options))
        {
            seed.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "creation-guard.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                Sha256 = new string('f', 64),
                StorageRef = "documents/creation-guard.pdf",
                CreatedAtUtc = nowUtc,
            });
            var config = new ProviderConfigEntity
            {
                Id = configId,
                Name = "Creation guard",
                ProviderType = "mineru-local",
                IsEnabled = true,
                CurrentVersionId = versionId,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            seed.ProviderConfigs.Add(config);
            seed.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
            {
                Id = versionId,
                ProviderConfigId = configId,
                ProviderConfig = config,
                VersionNumber = 1,
                BaseUrl = "http://provider.test/",
                CreatedAtUtc = nowUtc,
            });
            await seed.SaveChangesAsync();
        }

        await using var staleDocumentContext = new StructaDocDbContext(options);
        await using var staleProviderContext = new StructaDocDbContext(options);
        var staleDocument = await staleDocumentContext.Documents.SingleAsync(
            document => document.Id == documentId);
        var staleProvider = await staleProviderContext.ProviderConfigs.SingleAsync(
            config => config.Id == configId);

        await using (var creationContext = new StructaDocDbContext(options))
        {
            var result = await new EfCoreParseRunService(creationContext).CreateAsync(
                new ParseRunCreateRequest(
                    documentId,
                    configId,
                    "{}",
                    3,
                    CanonicalActor.Create(
                        CanonicalActor.AdministratorIssuer,
                        "11111111-1111-1111-1111-111111111111"),
                    null,
                    nowUtc));
            Assert.Equal(ParseRunCreationStatus.Created, result.Status);
        }

        staleDocument.LifecycleState = ResourceLifecycleStates.DeletionPending;
        staleDocument.DeletionRequestedAtUtc = nowUtc;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleDocumentContext.SaveChangesAsync());

        var executionStrategy = staleProviderContext.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction =
                await staleProviderContext.Database.BeginTransactionAsync();
            await staleProviderContext.ProviderConfigVersions
                .Where(version => version.ProviderConfigId == configId)
                .ExecuteDeleteAsync();
            staleProviderContext.ProviderConfigs.Remove(staleProvider);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => staleProviderContext.SaveChangesAsync());
            await transaction.RollbackAsync();
        });

        await using var cleanup = new StructaDocDbContext(options);
        await cleanup.ParseRuns.Where(run => run.DocumentId == documentId).ExecuteDeleteAsync();
        await cleanup.ProviderConfigVersions
            .Where(version => version.ProviderConfigId == configId)
            .ExecuteDeleteAsync();
        await cleanup.ProviderConfigs.Where(config => config.Id == configId).ExecuteDeleteAsync();
        await cleanup.Documents.Where(document => document.Id == documentId).ExecuteDeleteAsync();
    }

    private static async Task AssertSegmentMutationFencingAsync(
        DbContextOptions<StructaDocDbContext> options)
    {
        var nowUtc = DateTime.UtcNow;
        var documentId = Guid.NewGuid();
        var validRunId = Guid.NewGuid();
        var expiredRunId = Guid.NewGuid();
        var cancelledRunId = Guid.NewGuid();
        var takeoverRunId = Guid.NewGuid();
        var validLease = new ParseRunLease(
            validRunId,
            "segment-worker",
            1,
            nowUtc.AddMinutes(5));
        var expiredLease = new ParseRunLease(
            expiredRunId,
            "expired-segment-worker",
            1,
            nowUtc.AddSeconds(1));
        var cancelledLease = new ParseRunLease(
            cancelledRunId,
            "cancelled-segment-worker",
            1,
            nowUtc.AddMinutes(5));
        var takeoverLease = new ParseRunLease(
            takeoverRunId,
            "original-segment-worker",
            1,
            nowUtc.AddSeconds(-1));

        await using (var seed = new StructaDocDbContext(options))
        {
            seed.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "segment-fence-contract.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 128,
                Sha256 = new string('b', 64),
                StorageRef = "documents/segment-fence-contract.pdf",
                CreatedAtUtc = nowUtc,
            });
            seed.ParseRuns.AddRange(
                CreateRunningRun(validLease, documentId, nowUtc),
                CreateRunningRun(expiredLease, documentId, nowUtc),
                CreateRunningRun(cancelledLease, documentId, nowUtc),
                CreateRunningRun(
                    takeoverLease,
                    documentId,
                    nowUtc,
                    externalTaskId: "segment-takeover-task"));
            await seed.SaveChangesAsync();
        }

        var validSegment = Segment(validRunId, 0);
        ParseRunLease checkpointLease;
        await using (var dbContext = new StructaDocDbContext(options))
        {
            var store = new EfCoreParseSegmentMutationStore(dbContext);
            var createdLease = Assert.IsType<ParseRunLease>(await store.TryCreateAsync(
                validLease,
                [validSegment],
                nowUtc));
            Assert.Equal(validLease.ConcurrencyVersion + 1, createdLease.ConcurrencyVersion);

            checkpointLease = Assert.IsType<ParseRunLease>(
                await store.TryUpdateCheckpointAsync(
                    createdLease,
                    new ParseSegmentCheckpoint(
                        validSegment.Id,
                        "submitted",
                        "segment-task-1",
                        null),
                    nowUtc.AddSeconds(1)));
            Assert.Equal(createdLease.ConcurrencyVersion + 1, checkpointLease.ConcurrencyVersion);

            var persistedSegment = await dbContext.ParseSegments
                .AsNoTracking()
                .SingleAsync(segment => segment.Id == validSegment.Id);
            Assert.Equal("submitted", persistedSegment.Status);
            Assert.Equal("segment-task-1", persistedSegment.ExternalTaskId);
            Assert.Equal(
                nowUtc.AddSeconds(1),
                persistedSegment.UpdatedAtUtc,
                TimeSpan.FromMicroseconds(1));
        }

        await using (var dbContext = new StructaDocDbContext(options))
        {
            var expiredSegment = Segment(expiredRunId, 0);
            var store = new EfCoreParseSegmentMutationStore(dbContext);
            var createdLease = Assert.IsType<ParseRunLease>(await store.TryCreateAsync(
                expiredLease,
                [expiredSegment],
                nowUtc));
            Assert.Null(await store.TryUpdateCheckpointAsync(
                createdLease,
                new ParseSegmentCheckpoint(expiredSegment.Id, "submitted", "too-late", null),
                nowUtc.AddSeconds(2)));
            var lateSegment = Segment(expiredRunId, 1);
            Assert.Null(await store.TryCreateAsync(
                createdLease,
                [lateSegment],
                nowUtc.AddSeconds(2)));
            Assert.False(await dbContext.ParseSegments.AnyAsync(segment => segment.Id == lateSegment.Id));
            Assert.Equal(
                "created",
                await dbContext.ParseSegments
                    .Where(segment => segment.Id == expiredSegment.Id)
                    .Select(segment => segment.Status)
                    .SingleAsync());
        }

        await using (var dbContext = new StructaDocDbContext(options))
        {
            var cancelledSegment = Segment(cancelledRunId, 0);
            var store = new EfCoreParseSegmentMutationStore(dbContext);
            var createdLease = Assert.IsType<ParseRunLease>(await store.TryCreateAsync(
                cancelledLease,
                [cancelledSegment],
                nowUtc));
            var cancellation = await new EfCoreParseRunService(dbContext)
                .RequestCancellationAsync(cancelledRunId, nowUtc.AddSeconds(1));
            Assert.Equal(ParseRunCancellationStatus.Requested, cancellation.Status);
            Assert.Null(await store.TryUpdateCheckpointAsync(
                createdLease,
                new ParseSegmentCheckpoint(cancelledSegment.Id, "submitted", "too-late", null),
                nowUtc.AddSeconds(2)));
            var postCancellationSegment = Segment(cancelledRunId, 1);
            Assert.Null(await store.TryCreateAsync(
                createdLease,
                [postCancellationSegment],
                nowUtc.AddSeconds(2)));
            Assert.False(await dbContext.ParseSegments.AnyAsync(
                segment => segment.Id == postCancellationSegment.Id));
            Assert.Equal(
                "created",
                await dbContext.ParseSegments
                    .Where(segment => segment.Id == cancelledSegment.Id)
                    .Select(segment => segment.Status)
                    .SingleAsync());
        }

        await using (var dbContext = new StructaDocDbContext(options))
        {
            var takeoverSegment = Segment(takeoverRunId, 0);
            var store = new EfCoreParseSegmentMutationStore(dbContext);
            var preTakeoverLease = Assert.IsType<ParseRunLease>(await store.TryCreateAsync(
                takeoverLease,
                [takeoverSegment],
                nowUtc.AddSeconds(-2)));
            var recoveredLease = Assert.IsType<ParseRunLease>(
                await new EfCoreParseRunLeaseStore(dbContext).TryRecoverNextRunningAsync(
                    "takeover-segment-worker",
                    nowUtc,
                    TimeSpan.FromMinutes(5)));
            Assert.Equal(takeoverRunId, recoveredLease.ParseRunId);

            Assert.Null(await store.TryUpdateCheckpointAsync(
                preTakeoverLease,
                new ParseSegmentCheckpoint(takeoverSegment.Id, "submitted", "stale-task", null),
                nowUtc.AddSeconds(1)));
            var competingSegment = Segment(takeoverRunId, 1);
            Assert.Null(await store.TryCreateAsync(
                recoveredLease with { WorkerId = "competing-segment-worker" },
                [competingSegment],
                nowUtc.AddSeconds(1)));
            var takeoverCheckpointLease = Assert.IsType<ParseRunLease>(
                await store.TryUpdateCheckpointAsync(
                    recoveredLease,
                    new ParseSegmentCheckpoint(
                        takeoverSegment.Id,
                        "submitted",
                        "takeover-task",
                        null),
                    nowUtc.AddSeconds(1)));
            Assert.NotNull(await store.TryCreateAsync(
                takeoverCheckpointLease,
                [competingSegment],
                nowUtc.AddSeconds(2)));
            Assert.Equal(
                "takeover-task",
                await dbContext.ParseSegments
                    .Where(segment => segment.Id == takeoverSegment.Id)
                    .Select(segment => segment.ExternalTaskId)
                    .SingleAsync());
        }

        await using (var cleanup = new StructaDocDbContext(options))
        {
            await cleanup.ParseSegments
                .Where(segment => segment.ParseRun.DocumentId == documentId)
                .ExecuteDeleteAsync();
            await cleanup.ParseRuns
                .Where(parseRun => parseRun.DocumentId == documentId)
                .ExecuteDeleteAsync();
            await cleanup.Documents
                .Where(document => document.Id == documentId)
                .ExecuteDeleteAsync();
        }

        static ParseRunEntity CreateRunningRun(
            ParseRunLease lease,
            Guid documentId,
            DateTime createdAtUtc,
            string? externalTaskId = null) => new()
            {
                Id = lease.ParseRunId,
                DocumentId = documentId,
                Status = ParseRunStatuses.Running,
                Stage = ParseRunStages.Segmenting,
                ProviderType = "test-provider",
                ProviderConfigId = Guid.NewGuid(),
                ProviderConfigVersion = Guid.NewGuid(),
                OptionsJson = "{}",
                SourceMediaType = "application/pdf",
                SubmittedMediaType = "application/pdf",
                ExternalTaskId = externalTaskId,
                AttemptCount = 1,
                MaxAttempts = 3,
                NextAttemptAtUtc = createdAtUtc,
                ClaimedBy = lease.WorkerId,
                LeaseExpiresAtUtc = lease.LeaseExpiresAtUtc,
                CreatedAtUtc = createdAtUtc,
                StartedAtUtc = createdAtUtc,
                ConcurrencyVersion = lease.ConcurrencyVersion,
            };

        static ParseSegmentCreation Segment(Guid parseRunId, int index) => new(
            Guid.NewGuid(),
            index,
            index + 1,
            index + 1,
            $"parse-runs/{parseRunId:N}/segments/{index:D4}.pdf",
            128,
            new string('c', 64),
            "created");
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
