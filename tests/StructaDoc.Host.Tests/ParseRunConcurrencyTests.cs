using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Host.Tests;

public sealed class ParseRunConcurrencyTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Execution_slots_run_parse_runs_at_the_same_time()
    {
        var provider = new BlockingParseProvider(expectedConcurrency: 2);
        using var application = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Worker:ExecutionEnabled", "true");
            builder.UseSetting("Worker:MaxConcurrency", "2");
            builder.UseSetting("Worker:LeaseDuration", "00:00:30");
            builder.UseSetting("Worker:HeartbeatInterval", "00:00:00.200");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IParseProvider>();
                services.AddSingleton<IParseProvider>(provider);
            });
        });

        using var client = application.CreateClient();
        await SeedQueuedRunsAsync(application.Services, provider.ProviderType, count: 2);

        // Each slot holds its Parse Run inside the Provider call until both have arrived. A Host
        // that still executes one Parse Run at a time can never satisfy this, so it times out.
        await provider.AllActive.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, provider.PeakConcurrency);
    }

    private static async Task SeedQueuedRunsAsync(
        IServiceProvider services,
        string providerType,
        int count)
    {
        await using var scope = services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var nowUtc = DateTime.UtcNow;
        var configId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        dbContext.ProviderConfigs.Add(new ProviderConfigEntity
        {
            Id = configId,
            Name = $"Concurrency {configId:N}",
            ProviderType = providerType,
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

        for (var index = 0; index < count; index++)
        {
            var parseRunId = Guid.NewGuid();
            var sourceBytes = "%PDF-1.7\nconcurrency-test"u8.ToArray();
            await using var source = new MemoryStream(sourceBytes, writable: false);
            var stored = await storage.WriteAsync(
                $"documents/{parseRunId:N}/source.pdf",
                source,
                sourceBytes.Length);
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = $"concurrency-{index}.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = stored.SizeBytes,
                Sha256 = stored.Sha256,
                StorageRef = stored.StorageRef,
                CreatedAtUtc = nowUtc,
            };
            dbContext.Documents.Add(document);
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = parseRunId,
                DocumentId = document.Id,
                Status = ParseRunStatuses.Queued,
                ProviderType = providerType,
                ProviderConfigId = configId,
                ProviderConfigVersion = versionId,
                OptionsJson = "{}",
                SourceMediaType = document.MediaType,
                SubmittedMediaType = document.MediaType,
                MaxAttempts = 1,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private sealed class BlockingParseProvider(int expectedConcurrency) : IParseProvider
    {
        private int active;
        private int peak;
        private int taskNumber;

        public TaskCompletionSource AllActive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakConcurrency => Volatile.Read(ref peak);

        public string ProviderType => ProviderTypes.MinerULocal;

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderSubmissionCheckpoint?>(null);

        public async Task<ProviderSubmission> SubmitAsync(
            ProviderExecutionConfiguration configuration,
            Guid parseRunId,
            ProviderDocumentSource source,
            string optionsJson,
            ProviderSubmissionCheckpoint? checkpoint,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, current);
            if (current >= expectedConcurrency)
            {
                AllActive.TrySetResult();
            }

            try
            {
                await AllActive.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }

            return new ProviderSubmission($"concurrency-task-{Interlocked.Increment(ref taskNumber)}");
        }

        public Task<ProviderTaskStatus> GetStatusAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderTaskStatus(ProviderTaskState.Succeeded));

        public Task<ProviderResultContent> OpenResultAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default)
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("full.md").Open());
                writer.Write("# Concurrency result");
            }

            stream.Position = 0;
            return Task.FromResult(new ProviderResultContent(stream, "application/zip", "result.zip"));
        }

        public Task TryCancelAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
