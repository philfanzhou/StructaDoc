using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Providers;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Host.Tests;

public sealed partial class ParseRunExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V4_recovery_uses_the_saved_task_or_archive_without_resubmitting(bool savedArchive)
    {
        await using var server = await StubMinerUServer.StartAsync(runningResponses: 0, v4: true);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var provider = new MinerULocalParseProvider(http);
        var result = await ExecuteAsync(
            provider,
            providerBaseUrl: server.BaseUrl,
            backend: "v4:flash",
            seedExecution: async (services, id, _) =>
            {
                var db = services.GetRequiredService<StructaDocDbContext>();
                var run = await db.ParseRuns.SingleAsync(run => run.Id == id, cancellationToken: TestContext.Current.CancellationToken);
                run.ExternalTaskId = StubMinerUServer.TaskId;
                run.Stage = ParseRunStages.WaitingProvider;
                // Editing the current configuration must not reroute this run's v4 snapshot.
                var config = await db.ProviderConfigs.SingleAsync(config => config.Id == run.ProviderConfigId, TestContext.Current.CancellationToken);
                var newVersion = Guid.NewGuid();
                db.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
                {
                    Id = newVersion,
                    ProviderConfigId = config.Id,
                    VersionNumber = 2,
                    BaseUrl = server.BaseUrl,
                    Backend = "pipeline",
                    CreatedAtUtc = DateTime.UtcNow,
                });
                config.CurrentVersionId = newVersion;
                await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
                if (savedArchive)
                {
                    await using var content = new ProviderResultContent(new MemoryStream(StubMinerUServer.CreateResultArchive(v4: true)), "application/zip", "result.zip");
                    await services.GetRequiredService<IProviderResultIntake>().StoreArchiveAsync(id, content, cancellationToken: TestContext.Current.CancellationToken);
                }
            });
        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal(0, server.SubmitRequests);
        Assert.Equal(savedArchive ? 0 : 1, server.StatusRequests);
        Assert.Equal(savedArchive ? 0 : 1, server.ResultRequests);
        Assert.Equal([1, 2], result.Pages.Select(page => page.Number));
        Assert.True(result.ArtifactCount >= 5);
    }

    [Fact]
    public async Task V4_adapter_runs_on_the_capability_triggered_segmented_executor_path()
    {
        await using var server = await StubMinerUServer.StartAsync(runningResponses: 0, v4: true);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        // A deployment limit exercises segmentation without inventing a universal v4 page cap.
        var provider = new PageLimitedV4Provider(new MinerULocalParseProvider(http));
        var result = await ExecuteAsync(provider, largePdfPageCount: 4, executeLargePdfThroughExecutor: true, providerBaseUrl: server.BaseUrl, backend: "v4:flash");
        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal(2, server.SubmitRequests);
        Assert.Equal(2, server.StatusRequests);
        Assert.Equal(2, server.ResultRequests);
        Assert.Equal(2, result.Segments.Count);
        Assert.All(result.Segments, segment => Assert.Equal("normalized", segment.Status));
        Assert.Equal([1, 2, 3, 4], result.Pages.Select(page => page.Number));
    }

    private sealed class PageLimitedV4Provider(IParseProvider inner) : IParseProvider
    {
        public string ProviderType => inner.ProviderType;
        public async Task<ProviderCapabilities> GetCapabilitiesAsync(ProviderExecutionConfiguration configuration, CancellationToken cancellationToken = default)
        {
            var capabilities = await inner.GetCapabilitiesAsync(configuration, cancellationToken);
            return new ProviderCapabilities(capabilities.SupportedMediaTypes, capabilities.MaxFileBytes, 2, false);
        }
        public Task<ProviderSubmissionCheckpoint?> PrepareSubmissionAsync(ProviderExecutionConfiguration configuration, Guid parseRunId, ProviderDocumentSource source, string optionsJson, CancellationToken cancellationToken = default) => inner.PrepareSubmissionAsync(configuration, parseRunId, source, optionsJson, cancellationToken);
        public Task<ProviderSubmission> SubmitAsync(ProviderExecutionConfiguration configuration, Guid parseRunId, ProviderDocumentSource source, string optionsJson, ProviderSubmissionCheckpoint? checkpoint, CancellationToken cancellationToken = default) => inner.SubmitAsync(configuration, parseRunId, source, optionsJson, checkpoint, cancellationToken);
        public Task<ProviderTaskStatus> GetStatusAsync(ProviderExecutionConfiguration configuration, string externalTaskId, CancellationToken cancellationToken = default) => inner.GetStatusAsync(configuration, externalTaskId, cancellationToken);
        public Task<ProviderResultContent> OpenResultAsync(ProviderExecutionConfiguration configuration, string externalTaskId, CancellationToken cancellationToken = default) => inner.OpenResultAsync(configuration, externalTaskId, cancellationToken);
        public Task TryCancelAsync(ProviderExecutionConfiguration configuration, string externalTaskId, CancellationToken cancellationToken = default) => inner.TryCancelAsync(configuration, externalTaskId, cancellationToken);
    }
}
