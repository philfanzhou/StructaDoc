using System.IO.Compression;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Settings;
using StructaDoc.Application.Storage;
using StructaDoc.Contracts.Settings;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.Workers;

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
            builder.UseSetting("Worker:MaxConcurrency", "2");
            builder.UseSetting("Worker:LeaseDuration", "00:00:30");
            builder.UseSetting("Worker:HeartbeatInterval", "00:00:00.200");
            builder.ConfigureServices(services =>
            {
                // Execution is the subject here, and the shared host leaves its worker out so that
                // Parse Runs written as fixtures elsewhere are not claimed out from under them.
                services.AddHostedService<ParseRunExecutionWorker>();
                services.RemoveAll<IParseProvider>();
                services.AddSingleton<IParseProvider>(provider);
            });
        });

        using var client = application.CreateClient();
        await SeedQueuedRunsAsync(application.Services, provider.ProviderType, count: 2);

        // Each slot holds its Parse Run inside the Provider call until both have arrived. A Host
        // that still executes one Parse Run at a time can never satisfy this, so it times out.
        await provider.AllActive.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, provider.PeakConcurrency);
    }

    [Fact]
    public async Task Raising_the_concurrency_setting_without_a_restart_starts_a_second_claim()
    {
        var provider = new BlockingParseProvider(expectedConcurrency: 2);
        using var deployment = new SettingsTestDeployment();
        using var application = deployment.CreateFactory(builder =>
        {
            // MaxConcurrency is deliberately left unset: the Host starts on the shipped default of
            // one, and the raise to two has to come through the settings write this test makes.
            builder.UseSetting("Worker:Enabled", "true");
            builder.UseSetting("Worker:MaintenanceInterval", "00:00:00.100");
            builder.UseSetting("Worker:LeaseDuration", "00:00:30");
            builder.UseSetting("Worker:HeartbeatInterval", "00:00:00.200");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IParseProvider>();
                services.AddSingleton<IParseProvider>(provider);
            });
        });

        using var admin = await SettingsTestDeployment.SignedInClientAsync(application);
        var first = await SeedQueuedRunAsync(application.Services, provider.ProviderType);
        var second = await SeedQueuedRunAsync(application.Services, provider.ProviderType);

        // Under the shipped limit of one, exactly one of the two runs is claimed and held inside
        // the Provider call; the other stays queued. A host that ran both would satisfy the release
        // condition early and fail the count below.
        await provider.FirstSubmitted.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Submissions);
        await AssertStillQueuedAsync(application.Services, second, TimeSpan.FromSeconds(2));

        using (var raised = await admin.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, "2"),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            raised.EnsureSuccessStatusCode();
        }

        // The second claim starts on the running Host and brings the Provider to two at once, which
        // is the release condition both executions are waiting for.
        await provider.AllActive.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.PeakConcurrency);
    }

    [Fact]
    public async Task Lowering_the_concurrency_setting_stops_new_claims_and_keeps_what_is_running()
    {
        var provider = new GatedParseProvider();
        using var deployment = new SettingsTestDeployment();
        using var application = deployment.CreateFactory(builder =>
        {
            builder.UseSetting("Worker:Enabled", "true");
            builder.UseSetting("Worker:MaintenanceInterval", "00:00:00.100");
            builder.UseSetting("Worker:LeaseDuration", "00:00:30");
            builder.UseSetting("Worker:HeartbeatInterval", "00:00:00.200");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IParseProvider>();
                services.AddSingleton<IParseProvider>(provider);
            });
        });

        using var admin = await SettingsTestDeployment.SignedInClientAsync(application);
        await WriteConcurrencyAsync(admin, "2");

        // Two runs claimed and held inside the Provider, which is the state a lowered limit has to
        // leave alone: a started attempt runs to its own outcome under the lease model.
        var first = await SeedQueuedRunAsync(application.Services, provider.ProviderType);
        var second = await SeedQueuedRunAsync(application.Services, provider.ProviderType);
        await provider.SecondSubmitted.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        await WriteConcurrencyAsync(admin, "1");

        // A third run arrives after the ceiling came down. It stays queued — the worker keeps
        // polling but claims nothing while two executions are already inside a limit of one.
        var third = await SeedQueuedRunAsync(application.Services, provider.ProviderType);
        await AssertStillQueuedAsync(application.Services, third, TimeSpan.FromSeconds(2));
        Assert.Equal(2, provider.PeakConcurrency);

        // Letting the two running executions finish frees their slots, and only then is the third
        // claimed: the release admits exactly one more, never two.
        provider.Release();
        await provider.ThirdSubmitted.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.PeakConcurrency);
    }

    private static async Task WriteConcurrencyAsync(HttpClient admin, string value)
    {
        using var response = await admin.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.ParseMaxConcurrency, value),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Writing Worker:MaxConcurrency returned {(int)response.StatusCode}.");
    }

    private static async Task AssertStillQueuedAsync(
        IServiceProvider services,
        Guid parseRunId,
        TimeSpan window)
    {
        // The worker polls far inside this window, so a run still queued after it is one the Host
        // chose not to claim, not one it has not got around to.
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var status = await dbContext.ParseRuns
                .Where(run => run.Id == parseRunId)
                .Select(run => run.Status)
                .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ParseRunStatuses.Queued, status);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
    }

    private static async Task SeedQueuedRunsAsync(
        IServiceProvider services,
        string providerType,
        int count)
    {
        for (var index = 0; index < count; index++)
        {
            await SeedQueuedRunAsync(services, providerType);
        }
    }

    private static async Task<Guid> SeedQueuedRunAsync(
        IServiceProvider services,
        string providerType)
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
            OriginalFileName = $"concurrency-{parseRunId:N}.pdf",
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

        await dbContext.SaveChangesAsync();
        return parseRunId;
    }

    private sealed class BlockingParseProvider(int expectedConcurrency) : IParseProvider
    {
        private int active;
        private int peak;
        private int taskNumber;
        private int submitted;

        public TaskCompletionSource AllActive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstSubmitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakConcurrency => Volatile.Read(ref peak);

        public int Submissions => Volatile.Read(ref submitted);

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
            Interlocked.Increment(ref submitted);
            FirstSubmitted.TrySetResult();
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

    /// <summary>
    /// Holds every submission until the test releases it, and counts submissions as they arrive.
    /// The counts are what the concurrency contract is asserted against: how many runs the Host
    /// put inside the Provider at once, and that a third arrived only after the first two ended.
    /// </summary>
    private sealed class GatedParseProvider : IParseProvider
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private int peak;
        private int submissions;

        public TaskCompletionSource SecondSubmitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThirdSubmitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakConcurrency => Volatile.Read(ref peak);

        public string ProviderType => ProviderTypes.MinerULocal;

        public void Release() => release.TrySetResult();

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
            var count = Interlocked.Increment(ref submissions);
            if (count >= 2)
            {
                SecondSubmitted.TrySetResult();
            }

            if (count >= 3)
            {
                ThirdSubmitted.TrySetResult();
            }

            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, current);

            try
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }

            return new ProviderSubmission($"gated-task-{count}");
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
