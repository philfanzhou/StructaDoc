using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class ProviderExecutionAbstractionTests
{
    [Fact]
    public void Capabilities_normalize_media_types_and_enforce_limits()
    {
        var capabilities = new ProviderCapabilities(
            ["application/pdf", "IMAGE/PNG"],
            maxFileBytes: 1024,
            maxPages: 20,
            supportsCancellation: true);

        Assert.True(capabilities.SupportsMediaType("Application/Pdf; charset=binary"));
        Assert.True(capabilities.SupportsMediaType("image/png"));
        Assert.False(capabilities.SupportsMediaType("application/msword"));
        Assert.Equal(1024, capabilities.MaxFileBytes);
        Assert.Equal(20, capabilities.MaxPages);
        Assert.True(capabilities.SupportsCancellation);
    }

    [Fact]
    public void Credential_requires_explicit_reveal_and_is_redacted_by_default()
    {
        var credential = new ProviderCredential("provider-test-credential");

        Assert.Equal("[redacted]", credential.ToString());
        Assert.Equal("provider-test-credential", credential.Reveal());
    }

    [Theory]
    [InlineData("../result.zip")]
    [InlineData("result\\archive.zip")]
    [InlineData(" result.zip")]
    [InlineData("result\narchive.zip")]
    public void Provider_result_rejects_unsafe_display_file_names(string fileName)
    {
        Assert.Throws<ArgumentException>(() =>
            new ProviderResultContent(
                new MemoryStream("result"u8.ToArray()),
                "application/zip",
                fileName));
    }

    [Fact]
    public void Resolver_rejects_duplicate_types_and_returns_the_matching_provider()
    {
        var provider = new TestParseProvider(ProviderTypes.MinerULocal);
        var resolver = new ParseProviderResolver([provider]);

        Assert.Same(provider, resolver.Resolve(ProviderTypes.MinerULocal));
        Assert.Null(resolver.Resolve(ProviderTypes.MinerUCloud));
        Assert.Throws<InvalidOperationException>(() =>
            new ParseProviderResolver([
                provider,
                new TestParseProvider(ProviderTypes.MinerULocal),
            ]));
    }

    [Fact]
    public async Task Execution_context_uses_the_run_version_and_requires_the_current_lease()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "structadoc-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);

        try
        {
            var options = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(directoryPath, "structadoc.db")};Pooling=False",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly))
                .Options;
            var nowUtc = DateTime.UtcNow;
            var configId = Guid.NewGuid();
            var versionOneId = Guid.NewGuid();
            var versionTwoId = Guid.NewGuid();
            var parseRunId = Guid.NewGuid();

            await using (var dbContext = new StructaDocDbContext(options))
            {
                await dbContext.Database.MigrateAsync(
                    cancellationToken: TestContext.Current.CancellationToken);
                var document = new DocumentEntity
                {
                    Id = Guid.NewGuid(),
                    OriginalFileName = "source.pdf",
                    MediaType = "application/pdf",
                    Extension = ".pdf",
                    SizeBytes = 321,
                    Sha256 = new string('b', 64),
                    StorageRef = "documents/source.pdf",
                    CreatedAtUtc = nowUtc,
                };
                dbContext.Documents.Add(document);
                dbContext.ProviderConfigs.Add(new ProviderConfigEntity
                {
                    Id = configId,
                    Name = "Execution Test",
                    ProviderType = ProviderTypes.MinerUCloud,
                    IsEnabled = true,
                    CurrentVersionId = versionTwoId,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                dbContext.ProviderConfigVersions.AddRange(
                    new ProviderConfigVersionEntity
                    {
                        Id = versionOneId,
                        ProviderConfigId = configId,
                        VersionNumber = 1,
                        BaseUrl = "https://v1.provider.test/api/",
                        Model = "v1-model",
                        ProtectedCredential = "protected:version-one-secret",
                        CreatedAtUtc = nowUtc,
                    },
                    new ProviderConfigVersionEntity
                    {
                        Id = versionTwoId,
                        ProviderConfigId = configId,
                        VersionNumber = 2,
                        BaseUrl = "https://v2.provider.test/api/",
                        Model = "v2-model",
                        ProtectedCredential = "protected:version-two-secret",
                        CreatedAtUtc = nowUtc.AddMinutes(1),
                    });
                dbContext.ParseRuns.Add(new ParseRunEntity
                {
                    Id = parseRunId,
                    DocumentId = document.Id,
                    Status = ParseRunStatuses.Queued,
                    ProviderType = ProviderTypes.MinerUCloud,
                    ProviderConfigId = configId,
                    ProviderConfigVersion = versionOneId,
                    OptionsJson = "{\"ocr\":true}",
                    SourceMediaType = "application/pdf",
                    SubmittedMediaType = "application/pdf",
                    MaxAttempts = 3,
                    NextAttemptAtUtc = nowUtc,
                    CreatedAtUtc = nowUtc,
                });
                await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            ParseRunLease runningLease;
            await using (var dbContext = new StructaDocDbContext(options))
            {
                var leaseStore = new EfCoreParseRunLeaseStore(dbContext);
                var claimedLease = await leaseStore.TryClaimNextAsync(
                    "execution-test-worker",
                    nowUtc,
                    TimeSpan.FromMinutes(5),
                    TestContext.Current.CancellationToken);
                Assert.NotNull(claimedLease);

                var stateStore = new EfCoreParseRunStateStore(dbContext);
                runningLease = Assert.IsType<ParseRunLease>(await stateStore.TryStartAsync(
                    claimedLease,
                    ParseRunStages.Validating,
                    nowUtc.AddSeconds(1),
                    TestContext.Current.CancellationToken));
            }

            await using (var dbContext = new StructaDocDbContext(options))
            {
                var store = new EfCoreParseRunExecutionContextStore(
                    dbContext,
                    new TestSecretProtector(),
                    new TestSecretProtector());
                var context = await store.LoadAsync(
                    runningLease,
                    nowUtc.AddSeconds(2),
                    TestContext.Current.CancellationToken);

                Assert.NotNull(context);
                Assert.Equal(parseRunId, context.ParseRunId);
                Assert.Equal(versionOneId, context.ProviderConfiguration.VersionId);
                Assert.Equal(new Uri("https://v1.provider.test/api/"), context.ProviderConfiguration.BaseUri);
                Assert.Equal("v1-model", context.ProviderConfiguration.Model);
                Assert.Equal("version-one-secret", context.ProviderConfiguration.Credential?.Reveal());
                Assert.Equal("[redacted]", context.ProviderConfiguration.Credential?.ToString());
                Assert.Equal("documents/source.pdf", context.SourceStorageRef);
                Assert.Equal(ParseRunStages.Validating, context.Stage);
                Assert.DoesNotContain("version-one-secret", context.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("documents/source.pdf", context.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("source.pdf", context.ToString(), StringComparison.Ordinal);

                var staleContext = await store.LoadAsync(
                    runningLease with { ConcurrencyVersion = runningLease.ConcurrencyVersion - 1 },
                    nowUtc.AddSeconds(2),
                    TestContext.Current.CancellationToken);
                Assert.Null(staleContext);
            }

            ParseRunLease checkpointedLease;
            var checkpoint = new ProviderSubmissionCheckpoint(
                "batch-1",
                "https://upload.example/signed?secret=value");
            await using (var dbContext = new StructaDocDbContext(options))
            {
                var stateStore = new EfCoreParseRunStateStore(dbContext);
                var submittingLease = Assert.IsType<ParseRunLease>(
                    await stateStore.TryUpdateStageAsync(
                        runningLease,
                        ParseRunStages.Submitting,
                        nowUtc.AddSeconds(3),
                        TestContext.Current.CancellationToken));
                var checkpointStore = new EfCoreParseRunSubmissionCheckpointStore(
                    dbContext,
                    new TestSecretProtector());
                checkpointedLease = Assert.IsType<ParseRunLease>(
                    await checkpointStore.TrySaveAsync(
                        submittingLease,
                        checkpoint,
                        nowUtc.AddSeconds(4),
                        TestContext.Current.CancellationToken));

                var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync(
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(ParseRunStages.Submitting, persistedRun.Stage);
                Assert.Equal("batch-1", persistedRun.ExternalTaskId);
                Assert.Equal(
                    "protected:https://upload.example/signed?secret=value",
                    persistedRun.ProtectedSubmissionContinuation);
            }

            await using (var dbContext = new StructaDocDbContext(options))
            {
                var contextStore = new EfCoreParseRunExecutionContextStore(
                    dbContext,
                    new TestSecretProtector(),
                    new TestSecretProtector());
                var context = await contextStore.LoadAsync(
                    checkpointedLease,
                    nowUtc.AddSeconds(5),
                    TestContext.Current.CancellationToken);

                Assert.NotNull(context?.SubmissionCheckpoint);
                Assert.Equal("batch-1", context.SubmissionCheckpoint.ExternalTaskId);
                Assert.Equal(
                    "https://upload.example/signed?secret=value",
                    context.SubmissionCheckpoint.ContinuationToken);
                Assert.DoesNotContain(
                    "secret=value",
                    context.SubmissionCheckpoint.ToString(),
                    StringComparison.Ordinal);

                var checkpointStore = new EfCoreParseRunSubmissionCheckpointStore(
                    dbContext,
                    new TestSecretProtector());
                var completedLease = await checkpointStore.TryCompleteAsync(
                    checkpointedLease,
                    checkpoint,
                    nowUtc.AddSeconds(6),
                    TestContext.Current.CancellationToken);
                Assert.NotNull(completedLease);

                var persistedRun = await dbContext.ParseRuns.AsNoTracking().SingleAsync(
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(ParseRunStages.WaitingProvider, persistedRun.Stage);
                Assert.Equal("batch-1", persistedRun.ExternalTaskId);
                Assert.Null(persistedRun.ProtectedSubmissionContinuation);
            }
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private sealed class TestSecretProtector
        : IProviderSecretProtector, IProviderSubmissionProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue["protected:".Length..]
                : throw new InvalidOperationException("The test credential is not protected.");
    }

    private sealed class TestParseProvider(string providerType) : IParseProvider
    {
        public string ProviderType { get; } = providerType;

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            ProviderExecutionConfiguration configuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderSubmissionCheckpoint?> PrepareSubmissionAsync(
            ProviderExecutionConfiguration configuration,
            Guid parseRunId,
            ProviderDocumentSource source,
            string optionsJson,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderSubmission> SubmitAsync(
            ProviderExecutionConfiguration configuration,
            Guid parseRunId,
            ProviderDocumentSource source,
            string optionsJson,
            ProviderSubmissionCheckpoint? checkpoint,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderTaskStatus> GetStatusAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderResultContent> OpenResultAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TryCancelAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
