using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Settings;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.Tests;

public sealed class ParseRunLeaseHeartbeatTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Heartbeat_and_stage_updates_share_the_latest_lease_token()
    {
        using var client = factory.CreateClient();
        var initialLease = await AddRunningParseRunAsync();
        var heartbeat = factory.Services.GetRequiredService<ParseRunLeaseHeartbeat>();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);
        var submittingLease = await session.TryUpdateStageAsync(
            ParseRunStages.Submitting,
            TestContext.Current.CancellationToken);
        Assert.NotNull(submittingLease);

        var deadlineUtc = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadlineUtc
            && session.CurrentLease.ConcurrencyVersion == submittingLease.ConcurrencyVersion)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.False(session.IsLeaseLost);
        Assert.True(
            session.CurrentLease.ConcurrencyVersion > submittingLease.ConcurrencyVersion,
            "The heartbeat did not renew the lease within three seconds.");
        Assert.True(session.CurrentLease.LeaseExpiresAtUtc > submittingLease.LeaseExpiresAtUtc);
        var preparingLease = await session.TryUpdateStageAsync(
            ParseRunStages.PreparingSource,
            TestContext.Current.CancellationToken);
        Assert.NotNull(preparingLease);
        await session.DisposeAsync();
        var finalLease = session.CurrentLease;

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var persistedRun = await dbContext.ParseRuns
            .AsNoTracking()
            .SingleAsync(
                parseRun => parseRun.Id == initialLease.ParseRunId,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParseRunStages.PreparingSource, persistedRun.Stage);
        Assert.Equal(finalLease.ConcurrencyVersion, persistedRun.ConcurrencyVersion);
    }

    [Fact]
    public async Task Segment_mutations_advance_the_session_lease_used_by_heartbeat()
    {
        using var client = factory.CreateClient();
        var initialLease = await AddRunningParseRunAsync();
        var heartbeat = factory.Services.GetRequiredService<ParseRunLeaseHeartbeat>();
        var segmentId = Guid.NewGuid();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);
        Assert.NotNull(await session.TryUpdateStageAsync(
            ParseRunStages.Segmenting,
            TestContext.Current.CancellationToken));

        var creationLease = Assert.IsType<ParseRunLease>(
            await session.TryCreateSegmentsAsync(
                [new ParseSegmentCreation(
                    segmentId,
                    0,
                    1,
                    2,
                    $"parse-runs/{initialLease.ParseRunId:N}/segments/0000.pdf",
                    128,
                    new string('d', 64),
                    "created")],
                TestContext.Current.CancellationToken));
        var checkpointLease = Assert.IsType<ParseRunLease>(
            await session.TryUpdateSegmentCheckpointAsync(
                new ParseSegmentCheckpoint(
                    segmentId,
                    "submitted",
                    "segment-task-1",
                    null),
                TestContext.Current.CancellationToken));

        Assert.True(checkpointLease.ConcurrencyVersion > creationLease.ConcurrencyVersion);
        Assert.True(
            session.CurrentLease.ConcurrencyVersion >= checkpointLease.ConcurrencyVersion);

        var deadlineUtc = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadlineUtc
            && session.CurrentLease.ConcurrencyVersion == checkpointLease.ConcurrencyVersion)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.False(session.IsLeaseLost);
        Assert.True(session.CurrentLease.ConcurrencyVersion > checkpointLease.ConcurrencyVersion);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var persistedSegment = await dbContext.ParseSegments
            .AsNoTracking()
            .SingleAsync(
                segment => segment.Id == segmentId,
                TestContext.Current.CancellationToken);
        Assert.Equal("submitted", persistedSegment.Status);
        Assert.Equal("segment-task-1", persistedSegment.ExternalTaskId);
    }

    [Fact]
    public async Task Heartbeat_cancels_execution_when_the_lease_is_lost()
    {
        using var client = factory.CreateClient();
        var initialLease = await AddRunningParseRunAsync();
        var heartbeat = factory.Services.GetRequiredService<ParseRunLeaseHeartbeat>();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == initialLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.CancelRequested)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var leaseLost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session.ExecutionCancellationToken.Register(
            () => leaseLost.TrySetResult());
        await Task.WhenAny(leaseLost.Task, Task.Delay(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));

        Assert.True(
            session.IsLeaseLost,
            "The heartbeat did not observe the rejected lease renewal within ten seconds.");
        Assert.True(session.ExecutionCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Verification_outlasting_the_lease_completes_through_heartbeat_renewals()
    {
        using var application = CreateGatedStorageHost(out var gatedStorage);
        using var client = application.CreateClient();
        var initialLease = await AddRunningParseRunAsync(application.Services, leaseDuration: TimeSpan.FromSeconds(1));
        var bundle = await CreateStoredBundleAsync(gatedStorage, initialLease.ParseRunId);
        var heartbeat = application.Services.GetRequiredService<ParseRunLeaseHeartbeat>();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);
        var baselineVersion = session.CurrentLease.ConcurrencyVersion;
        var commitTask = session.TryCommitBundleAsync(bundle, TestContext.Current.CancellationToken);

        // Storage verification is now reading result objects and cannot finish until released.
        await gatedStorage.Opened.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        // With a 100 ms heartbeat and a 1 s lease, twelve renewals mean verification stayed in
        // progress for longer than one whole lease duration — impossible when verification held
        // the mutation gate, because renewal needs the same gate.
        var deadlineUtc = DateTime.UtcNow.AddSeconds(15);
        while (session.CurrentLease.ConcurrencyVersion < baselineVersion + 12
               && DateTime.UtcNow < deadlineUtc)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.False(session.IsLeaseLost);
        Assert.True(
            session.CurrentLease.ConcurrencyVersion >= baselineVersion + 12,
            "The heartbeat did not keep renewing the lease while storage verification was blocked.");
        gatedStorage.Release();
        var commit = await commitTask;

        Assert.NotNull(commit);
        Assert.Equal(ParseBundleCommitStatus.Committed, commit.Status);
        Assert.True(gatedStorage.LastStreamDisposed, "The verified storage stream was not disposed.");

        await using var scope = application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var persistedRun = await dbContext.ParseRuns
            .AsNoTracking()
            .SingleAsync(
                parseRun => parseRun.Id == initialLease.ParseRunId,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParseRunStatuses.Succeeded, persistedRun.Status);
        Assert.Null(persistedRun.ClaimedBy);
        Assert.Null(persistedRun.LeaseExpiresAtUtc);
        Assert.Equal(ParseBundleValidator.ComputeFingerprint(bundle), persistedRun.ResultSha256);
        Assert.Single(await dbContext.ParseArtifacts
            .Where(artifact => artifact.ParseRunId == initialLease.ParseRunId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Status_change_during_verification_prevents_publication()
    {
        using var application = CreateGatedStorageHost(out var gatedStorage);
        using var client = application.CreateClient();
        var initialLease = await AddRunningParseRunAsync(application.Services, leaseDuration: TimeSpan.FromSeconds(30));
        var bundle = await CreateStoredBundleAsync(gatedStorage, initialLease.ParseRunId);
        var heartbeat = application.Services.GetRequiredService<ParseRunLeaseHeartbeat>();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);
        var commitTask = session.TryCommitBundleAsync(bundle, TestContext.Current.CancellationToken);
        await gatedStorage.Opened.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

        // A cancellation request stops lease renewal, so the session observes a lost lease and
        // cancels the in-flight verification instead of publishing from a dead claim.
        await using (var scope = application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == initialLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.CancelRequested)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commitTask);
        Assert.True(session.IsLeaseLost);

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var persistedRun = await dbContext.ParseRuns
                .AsNoTracking()
                .SingleAsync(
                    parseRun => parseRun.Id == initialLease.ParseRunId,
                    cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ParseRunStatuses.CancelRequested, persistedRun.Status);
            Assert.Null(persistedRun.ResultSha256);
            Assert.Null(persistedRun.CompletedAtUtc);
            Assert.Empty(await dbContext.ParsePages
                .Where(page => page.ParseRunId == initialLease.ParseRunId)
                .ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await dbContext.ParseBlocks
                .Where(block => block.ParseRunId == initialLease.ParseRunId)
                .ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await dbContext.ParseArtifacts
                .Where(artifact => artifact.ParseRunId == initialLease.ParseRunId)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void Worker_options_require_heartbeat_to_be_shorter_than_the_lease()
    {
        var options = new ParseRunWorkerOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(1),
            HeartbeatInterval = TimeSpan.FromSeconds(1),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // Provider configuration selects where a requested Parse Run is processed.
    // There was once another execution switch in the administration page, defaulting to off,
    // which left requested runs queued without a failure. This is what would
    // notice one coming back: shipped options that run Workers, and a settings catalog with nothing
    // in it that stands between an administrator's decision and the Worker acting on it.
    [Fact]
    public void Nothing_switchable_stands_between_a_configured_provider_and_execution()
    {
        Assert.True(new ParseRunWorkerOptions().Enabled);

        Assert.DoesNotContain(
            SettingCatalog.All,
            definition => definition.Key.Equals(
                "Worker:ExecutionEnabled",
                StringComparison.OrdinalIgnoreCase));
    }

    private Task<ParseRunLease> AddRunningParseRunAsync() =>
        AddRunningParseRunAsync(factory.Services, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Builds a host whose result-object reads block until released, with a lease short enough
    /// that a blocked verification would lose the claim without heartbeat renewals.
    /// </summary>
    private WebApplicationFactory<Program> CreateGatedStorageHost(out GatedFileStorage gatedStorage)
    {
        var application = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Worker:LeaseDuration", "00:00:01");
            builder.UseSetting("Worker:HeartbeatInterval", "00:00:00.100");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider => new GatedFileStorage(
                    new LocalFileStorage(serviceProvider.GetRequiredService<FileStorageOptions>())));
            });
        });

        // Accessing Services builds the host, which materializes the singleton storage wrapper.
        gatedStorage = Assert.IsType<GatedFileStorage>(
            application.Services.GetRequiredService<IFileStorage>());
        return application;
    }

    private static async Task<ParseBundle> CreateStoredBundleAsync(
        IFileStorage storage,
        Guid parseRunId)
    {
        StoredFile markdown;
        await using (var content = new MemoryStream(
            Encoding.UTF8.GetBytes("# Heartbeat verification bundle")))
        {
            markdown = await storage.WriteAsync(
                $"results/{parseRunId:N}/full.md",
                content,
                1024 * 1024);
        }

        return new ParseBundle(
            ParseBundleValidator.CurrentSchemaVersion,
            parseRunId,
            [new ParsePage(1, 1000, 1400, "pixel")],
            [new ParseBlock(Guid.NewGuid(), 0, 1, "text", Content: "Heartbeat verification bundle")],
            [],
            [new ParseArtifact(
                Guid.NewGuid(),
                ArtifactTypes.Markdown,
                "full.md",
                "text/markdown",
                markdown.SizeBytes,
                markdown.Sha256,
                markdown.StorageRef)],
            "{\"providerType\":\"test-provider\"}");
    }

    private static async Task<ParseRunLease> AddRunningParseRunAsync(
        IServiceProvider services,
        TimeSpan leaseDuration)
    {
        var nowUtc = DateTime.UtcNow;
        var parseRunId = Guid.NewGuid();
        var workerId = $"heartbeat-worker-{parseRunId:N}";
        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "heartbeat-test.pdf",
            MediaType = "application/pdf",
            Extension = ".pdf",
            SizeBytes = 128,
            Sha256 = new string('a', 64),
            StorageRef = $"documents/{parseRunId:N}.pdf",
            CreatedAtUtc = nowUtc,
        };
        dbContext.Documents.Add(document);
        dbContext.ParseRuns.Add(new ParseRunEntity
        {
            Id = parseRunId,
            DocumentId = document.Id,
            Status = ParseRunStatuses.Running,
            Stage = ParseRunStages.Validating,
            ProviderType = "heartbeat-test-provider",
            ProviderConfigId = Guid.NewGuid(),
            ProviderConfigVersion = Guid.NewGuid(),
            OptionsJson = "{}",
            SourceMediaType = "application/pdf",
            SubmittedMediaType = "application/pdf",
            AttemptCount = 1,
            MaxAttempts = 3,
            NextAttemptAtUtc = nowUtc,
            ClaimedBy = workerId,
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
            CreatedAtUtc = nowUtc,
            StartedAtUtc = nowUtc,
            ConcurrencyVersion = 1,
        });
        await dbContext.SaveChangesAsync();

        return new ParseRunLease(parseRunId, workerId, 1, leaseExpiresAtUtc);
    }

    /// <summary>
    /// Storage wrapper that parks result-object reads until the test releases them, so a
    /// verification in progress can be observed and steered without timing-sensitive sleeps.
    /// </summary>
    private sealed class GatedFileStorage(IFileStorage inner) : IFileStorage
    {
        private readonly TaskCompletionSource opened = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int lastStreamDisposed;

        public Task Opened => opened.Task;

        public bool LastStreamDisposed => Volatile.Read(ref lastStreamDisposed) != 0;

        public void Release() => release.TrySetResult();

        public Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(storageRef, content, maxBytes, cancellationToken);

        public async Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default)
        {
            opened.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            var stream = await inner.OpenReadAsync(storageRef, cancellationToken);
            return new TrackingStream(stream, () => Volatile.Write(ref lastStreamDisposed, 1));
        }

        public Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default) =>
            inner.DeleteIfExistsAsync(storageRef, cancellationToken);

        private sealed class TrackingStream(Stream inner, Action onDisposed) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override void Flush() => inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) =>
                inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) =>
                inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void SetLength(long value) => inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) =>
                inner.Write(buffer, offset, count);

            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                onDisposed();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    onDisposed();
                }

                base.Dispose(disposing);
            }
        }
    }
}
