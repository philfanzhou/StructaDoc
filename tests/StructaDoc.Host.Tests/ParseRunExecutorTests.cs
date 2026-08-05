using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.Workers;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;
using StructaDoc.Infrastructure.Persistence.ParseRuns;

namespace StructaDoc.Host.Tests;

public sealed class ParseRunExecutorTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Executor_runs_provider_result_pipeline_to_canonical_success()
    {
        var provider = new TestParseProvider(failSubmission: false);
        var result = await ExecuteAsync(provider);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Equal("local-task-1", result.ExternalTaskId);
        Assert.NotNull(result.ResultSha256);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(1, provider.StatusCount);
        Assert.Equal(1, provider.ResultCount);
        Assert.True(result.ArtifactCount >= 2);
    }

    [Fact]
    public async Task Executor_does_not_retry_an_atomic_submission_with_unknown_outcome()
    {
        var provider = new TestParseProvider(failSubmission: true);
        var result = await ExecuteAsync(provider);

        Assert.Equal(ParseRunStatuses.Failed, result.Status);
        Assert.Equal("provider-submission-outcome-unknown", result.ErrorCode);
        Assert.Null(result.ExternalTaskId);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(0, provider.StatusCount);
        Assert.Equal(0, provider.ResultCount);
    }

    [Fact]
    public async Task Executor_persists_and_clears_a_checkpoint_before_cloud_upload()
    {
        var provider = new TestParseProvider(failSubmission: false, useCheckpoint: true);
        var result = await ExecuteAsync(provider);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal("cloud-batch-1", result.ExternalTaskId);
        Assert.Null(result.ProtectedSubmissionContinuation);
        Assert.Equal(1, provider.PrepareCount);
        Assert.Equal(1, provider.SubmitCount);
        Assert.True(provider.SubmitObservedCheckpoint);
    }

    private async Task<ExecutionResult> ExecuteAsync(TestParseProvider provider)
    {
        using var application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IParseProvider>();
                services.AddSingleton<IParseProvider>(provider);
            }));
        using var client = application.CreateClient();
        var parseRunId = Guid.NewGuid();
        var sourceStorageRef = $"documents/{parseRunId:N}/source.pdf";
        var sourceBytes = "%PDF-1.7\nexecutor-test"u8.ToArray();

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            await using var source = new MemoryStream(sourceBytes, writable: false);
            var storedSource = await storage.WriteAsync(
                sourceStorageRef,
                source,
                sourceBytes.Length);
            var nowUtc = DateTime.UtcNow;
            var configId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "executor-test.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = storedSource.SizeBytes,
                Sha256 = storedSource.Sha256,
                StorageRef = storedSource.StorageRef,
                CreatedAtUtc = nowUtc,
            };
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            dbContext.Documents.Add(document);
            dbContext.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = configId,
                Name = $"Executor Test {parseRunId:N}",
                ProviderType = provider.ProviderType,
                IsEnabled = true,
                CurrentVersionId = versionId,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            });
            dbContext.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
            {
                Id = versionId,
                ProviderConfigId = configId,
                VersionNumber = 1,
                BaseUrl = "https://provider.example/",
                CreatedAtUtc = nowUtc,
            });
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = parseRunId,
                DocumentId = document.Id,
                Status = ParseRunStatuses.Queued,
                ProviderType = provider.ProviderType,
                ProviderConfigId = configId,
                ProviderConfigVersion = versionId,
                OptionsJson = "{}",
                SourceMediaType = document.MediaType,
                SubmittedMediaType = document.MediaType,
                MaxAttempts = 3,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
            });
            await dbContext.SaveChangesAsync();
        }

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var nowUtc = DateTime.UtcNow;
            var leaseStore = scope.ServiceProvider.GetRequiredService<IParseRunLeaseStore>();
            var lease = Assert.IsType<ParseRunLease>(await leaseStore.TryClaimNextAsync(
                $"executor-test-{parseRunId:N}",
                nowUtc,
                TimeSpan.FromMinutes(2)));
            await scope.ServiceProvider.GetRequiredService<ParseRunExecutor>().ExecuteAsync(
                lease,
                alreadyRunning: false);
        }

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var run = await dbContext.ParseRuns
                .AsNoTracking()
                .SingleAsync(item => item.Id == parseRunId);
            var artifactCount = await dbContext.ParseArtifacts.CountAsync(
                artifact => artifact.ParseRunId == parseRunId);
            return new ExecutionResult(
                run.Status,
                run.ErrorCode,
                run.ExternalTaskId,
                run.ProtectedSubmissionContinuation,
                run.ResultSha256,
                artifactCount);
        }
    }

    private sealed class TestParseProvider(
        bool failSubmission,
        bool useCheckpoint = false) : IParseProvider
    {
        public string ProviderType => useCheckpoint
            ? ProviderTypes.MinerUCloud
            : ProviderTypes.MinerULocal;

        public int PrepareCount { get; private set; }

        public int SubmitCount { get; private set; }

        public int StatusCount { get; private set; }

        public int ResultCount { get; private set; }

        public bool SubmitObservedCheckpoint { get; private set; }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            ProviderExecutionConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.FromResult(new ProviderCapabilities(
                ["application/pdf"],
                maxFileBytes: 1024 * 1024,
                maxPages: null,
                supportsCancellation: false));

        public Task<ProviderSubmissionCheckpoint?> PrepareSubmissionAsync(
            ProviderExecutionConfiguration configuration,
            Guid parseRunId,
            ProviderDocumentSource source,
            string optionsJson,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult<ProviderSubmissionCheckpoint?>(
                useCheckpoint
                    ? new ProviderSubmissionCheckpoint(
                        "cloud-batch-1",
                        "https://upload.example/signed?secret=value")
                    : null);
        }

        public Task<ProviderSubmission> SubmitAsync(
            ProviderExecutionConfiguration configuration,
            Guid parseRunId,
            ProviderDocumentSource source,
            string optionsJson,
            ProviderSubmissionCheckpoint? checkpoint,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            SubmitObservedCheckpoint = checkpoint is not null;
            if (failSubmission)
            {
                throw new ProviderException(
                    "provider-network-error",
                    "The Provider request failed due to a network error.",
                    ProviderFailureCategory.Transient);
            }

            return Task.FromResult(new ProviderSubmission(
                useCheckpoint ? "cloud-batch-1" : "local-task-1"));
        }

        public Task<ProviderTaskStatus> GetStatusAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default)
        {
            StatusCount++;
            return Task.FromResult(new ProviderTaskStatus(ProviderTaskState.Succeeded));
        }

        public Task<ProviderResultContent> OpenResultAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default)
        {
            ResultCount++;
            return Task.FromResult(new ProviderResultContent(
                CreateResultArchive(),
                "application/zip",
                "result.zip"));
        }

        public Task TryCancelAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static Stream CreateResultArchive()
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var markdown = archive.CreateEntry("full.md");
                using var writer = new StreamWriter(markdown.Open());
                writer.Write("# Executor result");
            }

            stream.Position = 0;
            return stream;
        }
    }

    private sealed record ExecutionResult(
        string Status,
        string? ErrorCode,
        string? ExternalTaskId,
        string? ProtectedSubmissionContinuation,
        string? ResultSha256,
        int ArtifactCount);
}
