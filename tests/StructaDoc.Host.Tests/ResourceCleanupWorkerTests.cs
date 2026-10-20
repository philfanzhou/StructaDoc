using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Ownership fencing of the cleanup Worker. A cleanup attempt can outlive its five-minute reclaim
/// deadline, so these tests drive the real <see cref="ResourceCleanupWorker"/> pipeline against a
/// temporary SQLite database with a manual clock and a scripted storage double: each scenario parks
/// the first attempt inside object deletion, lets a second claim take over, and then observes what
/// the stale attempt may still write. The Worker's loop is not hosted, so nothing runs unless a
/// scenario invokes one attempt explicitly.
/// </summary>
public sealed class ResourceCleanupWorkerTests
{
    private static readonly MethodInfo ProcessOneAsyncMethod =
        typeof(ResourceCleanupWorker).GetMethod(
            "ProcessOneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "ResourceCleanupWorker.ProcessOneAsync was not found by the test harness.");

    [Fact]
    public async Task Late_failure_after_reclaim_completion_cannot_overwrite_the_terminal_state()
    {
        var startUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (clock, storage) = Prepare(startUtc);
        await using var environment = await CleanupTestEnvironment.CreateAsync(clock, storage);
        var seeded = await environment.SeedParseRunJobAsync(startUtc);
        storage.Gate(sequence: 0, failWhenReleased: true);
        var worker = environment.CreateWorker();

        // Attempt A claims the job (concurrency version 1) and parks inside its first deletion.
        var attemptA = ProcessOneAsync(worker, CancellationToken.None);
        await storage.DeletedAsync(sequence: 0);

        // The reclaim deadline lapses; attempt B claims (version 2) and completes (version 3).
        clock.AdvanceTo(startUtc.AddMinutes(6));
        var attemptB = ProcessOneAsync(worker, CancellationToken.None);
        await attemptB;

        // Releasing A fails its deletion; its retry must not disturb the completed state.
        storage.Release(sequence: 0);
        await attemptA;

        var persisted = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Completed, persisted.Status);
        Assert.Null(persisted.ErrorMessage);
        Assert.Equal(2, persisted.AttemptCount);
        Assert.Equal(3, persisted.ConcurrencyVersion);
        Assert.Equal(startUtc.AddMinutes(11), persisted.NextAttemptAtUtc);
        Assert.Equal(startUtc.AddMinutes(6), persisted.UpdatedAtUtc);
        await environment.AssertParseRunRemovedAsync(seeded);
    }

    [Fact]
    public async Task Late_failure_cannot_disturb_a_newer_running_claim()
    {
        var startUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (clock, storage) = Prepare(startUtc);
        await using var environment = await CleanupTestEnvironment.CreateAsync(clock, storage);
        var seeded = await environment.SeedParseRunJobAsync(startUtc);
        storage.Gate(sequence: 0, failWhenReleased: true);
        storage.Gate(sequence: 1, failWhenReleased: false);
        var worker = environment.CreateWorker();

        var attemptA = ProcessOneAsync(worker, CancellationToken.None);
        await storage.DeletedAsync(sequence: 0);

        // Attempt B reclaims (version 2) and parks inside its own first deletion.
        clock.AdvanceTo(startUtc.AddMinutes(6));
        var attemptB = ProcessOneAsync(worker, CancellationToken.None);
        await storage.DeletedAsync(sequence: 1);

        storage.Release(sequence: 0);
        await attemptA;

        var duringTakeover = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Running, duringTakeover.Status);
        Assert.Null(duringTakeover.ErrorMessage);
        Assert.Equal(2, duringTakeover.AttemptCount);
        Assert.Equal(2, duringTakeover.ConcurrencyVersion);
        Assert.Equal(startUtc.AddMinutes(11), duringTakeover.NextAttemptAtUtc);
        Assert.Equal(startUtc.AddMinutes(6), duringTakeover.UpdatedAtUtc);

        storage.Release(sequence: 1);
        await attemptB;

        var persisted = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Completed, persisted.Status);
        Assert.Null(persisted.ErrorMessage);
        await environment.AssertParseRunRemovedAsync(seeded);
    }

    [Fact]
    public async Task Owned_failure_schedules_one_backoff_retry()
    {
        var startUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (clock, storage) = Prepare(startUtc);
        await using var environment = await CleanupTestEnvironment.CreateAsync(clock, storage);
        var seeded = await environment.SeedParseRunJobAsync(startUtc);
        storage.FailEveryDeletion();
        var worker = environment.CreateWorker();

        await ProcessOneAsync(worker, CancellationToken.None);

        var persisted = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.RetryWait, persisted.Status);
        Assert.Equal("Scripted storage failure.", persisted.ErrorMessage);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal(2, persisted.ConcurrencyVersion);
        Assert.Equal(startUtc.AddSeconds(2), persisted.NextAttemptAtUtc);
        Assert.Equal(startUtc, persisted.UpdatedAtUtc);
        await environment.AssertRowsIntactAsync(seeded);
    }

    [Fact]
    public async Task Shutdown_cancellation_leaves_the_job_recoverable_through_stale_reclaim()
    {
        var startUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (clock, storage) = Prepare(startUtc);
        await using var environment = await CleanupTestEnvironment.CreateAsync(clock, storage);
        var seeded = await environment.SeedParseRunJobAsync(startUtc);
        storage.Gate(sequence: 0, failWhenReleased: false);
        var worker = environment.CreateWorker();
        using var cancellation = new CancellationTokenSource();

        var attemptA = ProcessOneAsync(worker, cancellation.Token);
        await storage.DeletedAsync(sequence: 0);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attemptA);

        // The cancelled attempt leaves the job running under its own claim with a future deadline,
        // which is exactly what the existing stale-running recovery reclaims later.
        var stale = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Running, stale.Status);
        Assert.Null(stale.ErrorMessage);
        Assert.Equal(1, stale.AttemptCount);
        Assert.Equal(1, stale.ConcurrencyVersion);
        Assert.Equal(startUtc.AddMinutes(5), stale.NextAttemptAtUtc);

        storage.Release(sequence: 0);
        clock.AdvanceTo(startUtc.AddMinutes(6));
        await ProcessOneAsync(worker, CancellationToken.None);

        var persisted = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Completed, persisted.Status);
        Assert.Equal(2, persisted.AttemptCount);
        await environment.AssertParseRunRemovedAsync(seeded);
    }

    [Fact]
    public async Task Late_success_from_a_lost_claim_removes_no_rows_and_completes_nothing()
    {
        var startUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (clock, storage) = Prepare(startUtc);
        await using var environment = await CleanupTestEnvironment.CreateAsync(clock, storage);
        var seeded = await environment.SeedDocumentJobAsync(startUtc);
        storage.Gate(sequence: 1, failWhenReleased: false);
        storage.Gate(sequence: 2, failWhenReleased: false);
        var worker = environment.CreateWorker();

        // Attempt A deletes the first object (sequence 0) and parks on the second (sequence 1).
        var attemptA = ProcessOneAsync(worker, CancellationToken.None);
        await storage.DeletedAsync(sequence: 1);

        // Attempt B reclaims (version 2) and parks inside its own first deletion (sequence 2),
        // so the relational rows still exist when A wakes up.
        clock.AdvanceTo(startUtc.AddMinutes(6));
        var attemptB = ProcessOneAsync(worker, CancellationToken.None);
        await storage.DeletedAsync(sequence: 2);

        storage.Release(sequence: 1);
        await attemptA;

        // A's completion loses the claim's concurrency check: its staged row removals roll back
        // with the failed save, and its fenced retry schedules nothing.
        var duringTakeover = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Running, duringTakeover.Status);
        Assert.Null(duringTakeover.ErrorMessage);
        Assert.Equal(2, duringTakeover.AttemptCount);
        Assert.Equal(2, duringTakeover.ConcurrencyVersion);
        Assert.Equal(startUtc.AddMinutes(11), duringTakeover.NextAttemptAtUtc);
        await environment.AssertRowsIntactAsync(seeded);

        storage.Release(sequence: 2);
        await attemptB;

        var persisted = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Completed, persisted.Status);
        Assert.Null(persisted.ErrorMessage);
        Assert.Equal(3, persisted.ConcurrencyVersion);
        await environment.AssertDocumentRemovedAsync(seeded);
        Assert.Equal(4, storage.DeletionCalls);
    }

    [Fact]
    public async Task Late_success_after_completion_leaves_the_job_untouched()
    {
        var startUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (clock, storage) = Prepare(startUtc);
        await using var environment = await CleanupTestEnvironment.CreateAsync(clock, storage);
        var seeded = await environment.SeedDocumentJobAsync(startUtc);
        storage.Gate(sequence: 1, failWhenReleased: false);
        var worker = environment.CreateWorker();

        // Attempt A deletes the first object (sequence 0) and parks on the second (sequence 1).
        var attemptA = ProcessOneAsync(worker, CancellationToken.None);
        await storage.DeletedAsync(sequence: 1);

        // Attempt B reclaims, repeats both deletions idempotently (sequences 2 and 3), removes the
        // relational rows, and completes through its own claim.
        clock.AdvanceTo(startUtc.AddMinutes(6));
        var attemptB = ProcessOneAsync(worker, CancellationToken.None);
        await attemptB;

        storage.Release(sequence: 1);
        await attemptA;

        var persisted = await environment.LoadJobAsync(seeded.Job.Id);
        Assert.Equal(CleanupJobStatuses.Completed, persisted.Status);
        Assert.Null(persisted.ErrorMessage);
        Assert.Equal(2, persisted.AttemptCount);
        Assert.Equal(3, persisted.ConcurrencyVersion);
        Assert.Equal(startUtc.AddMinutes(11), persisted.NextAttemptAtUtc);
        Assert.Equal(startUtc.AddMinutes(6), persisted.UpdatedAtUtc);
        await environment.AssertDocumentRemovedAsync(seeded);
        Assert.Equal(4, storage.DeletionCalls);
    }

    private static (ManualTimeProvider Clock, ScriptedFileStorage Storage) Prepare(DateTime startUtc) =>
        (new ManualTimeProvider(startUtc), new ScriptedFileStorage());

    private static Task<bool> ProcessOneAsync(
        ResourceCleanupWorker worker,
        CancellationToken cancellationToken) =>
        (Task<bool>)ProcessOneAsyncMethod.Invoke(worker, [cancellationToken])!;

    /// <summary>
    /// Deterministic clock: the Worker reads claim, completion, and retry timestamps from it, so a
    /// scenario advances time instead of waiting for the five-minute reclaim deadline.
    /// </summary>
    private sealed class ManualTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime currentUtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

        public void AdvanceTo(DateTime value) =>
            currentUtcNow = DateTime.SpecifyKind(value, DateTimeKind.Utc);

        public override DateTimeOffset GetUtcNow() => new(currentUtcNow, TimeSpan.Zero);
    }

    /// <summary>
    /// Storage double that scripts deletion attempts by sequence number. A gated sequence signals
    /// that the attempt reached it, then parks the call until released — honoring cancellation
    /// while parked — and can be set to throw a scripted IOException once released; ungated calls
    /// are immediate idempotent deletions.
    /// </summary>
    private sealed class ScriptedFileStorage : IFileStorage
    {
        private readonly Dictionary<int, (TaskCompletionSource Opened, TaskCompletionSource Release)> gates = new();
        private readonly HashSet<int> failingSequences = new();
        private int nextCall;
        private bool failEveryDeletion;

        public int DeletionCalls => Volatile.Read(ref nextCall);

        public void Gate(int sequence, bool failWhenReleased)
        {
            gates[sequence] = (
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (failWhenReleased)
            {
                failingSequences.Add(sequence);
            }
        }

        public Task DeletedAsync(int sequence) => gates[sequence].Opened.Task;

        public void Release(int sequence) => gates[sequence].Release.TrySetResult();

        public void FailEveryDeletion() => failEveryDeletion = true;

        public Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default)
        {
            var sequence = Interlocked.Increment(ref nextCall) - 1;
            if (gates.TryGetValue(sequence, out var gate))
            {
                gate.Opened.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
                if (failingSequences.Contains(sequence))
                {
                    throw new IOException("Scripted storage failure.");
                }
            }

            if (failEveryDeletion)
            {
                throw new IOException("Scripted storage failure.");
            }
        }
    }

    private sealed record SeededCleanupJob(CleanupJobEntity Job, Guid DocumentId, Guid ParseRunId);

    /// <summary>
    /// One temporary SQLite database with the Worker's two scope dependencies registered. Each
    /// Worker attempt creates its own service scope, so two overlapping attempts use independent
    /// change trackers exactly like two loop iterations on separate Workers would.
    /// </summary>
    private sealed class CleanupTestEnvironment : IAsyncDisposable
    {
        private readonly string directoryPath;
        private readonly ServiceProvider serviceProvider;

        private CleanupTestEnvironment(string directoryPath, ServiceProvider serviceProvider)
        {
            this.directoryPath = directoryPath;
            this.serviceProvider = serviceProvider;
        }

        public static async Task<CleanupTestEnvironment> CreateAsync(
            TimeProvider clock,
            IFileStorage storage)
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "structadoc-cleanup-worker-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var services = new ServiceCollection();
            services.AddDbContext<StructaDocDbContext>(options =>
                PersistenceServiceCollectionExtensions.ConfigureDatabase(
                    options,
                    new DatabaseOptions
                    {
                        Provider = DatabaseProvider.Sqlite,
                        ConnectionString =
                            $"Data Source={Path.Combine(directoryPath, "structadoc.db")};Pooling=False",
                        ApplyMigrationsOnStartup = false,
                    }));
            services.AddSingleton(clock);
            services.AddSingleton(storage);
            var serviceProvider = services.BuildServiceProvider();
            var environment = new CleanupTestEnvironment(directoryPath, serviceProvider);
            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
                await dbContext.Database.MigrateAsync();
            }

            return environment;
        }

        public ResourceCleanupWorker CreateWorker() => new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            NullLogger<ResourceCleanupWorker>.Instance);

        public async Task<SeededCleanupJob> SeedParseRunJobAsync(DateTime nowUtc)
        {
            var parseRunId = Guid.NewGuid();
            return await SeedAsync(
                CleanupTargetTypes.ParseRun,
                parseRunId,
                [$"parse-runs/{parseRunId:N}/provider/result.zip"],
                nowUtc);
        }

        public async Task<SeededCleanupJob> SeedDocumentJobAsync(DateTime nowUtc)
        {
            var parseRunId = Guid.NewGuid();
            return await SeedAsync(
                CleanupTargetTypes.Document,
                parseRunId,
                ["documents/original.pdf", $"parse-runs/{parseRunId:N}/provider/result.zip"],
                nowUtc);
        }

        private async Task<SeededCleanupJob> SeedAsync(
            string targetType,
            Guid parseRunId,
            IReadOnlyList<string> storageRefs,
            DateTime nowUtc)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var documentId = Guid.NewGuid();
            dbContext.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "cleanup-fence-test.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 128,
                Sha256 = new string('a', 64),
                StorageRef = "documents/original.pdf",
                CreatedAtUtc = nowUtc,
            });
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = parseRunId,
                DocumentId = documentId,
                Status = ParseRunStatuses.Succeeded,
                ProviderType = "test-provider",
                ProviderConfigId = Guid.NewGuid(),
                ProviderConfigVersion = Guid.NewGuid(),
                OptionsJson = "{}",
                SourceMediaType = "application/pdf",
                SubmittedMediaType = "application/pdf",
                MaxAttempts = 3,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                CompletedAtUtc = nowUtc,
            });
            var job = new CleanupJobEntity
            {
                Id = Guid.NewGuid(),
                TargetType = targetType,
                TargetId = targetType == CleanupTargetTypes.ParseRun ? parseRunId : documentId,
                StorageRefsJson = JsonSerializer.Serialize(storageRefs),
                Status = CleanupJobStatuses.Pending,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            dbContext.CleanupJobs.Add(job);
            await dbContext.SaveChangesAsync();
            return new SeededCleanupJob(job, documentId, parseRunId);
        }

        public async Task<CleanupJobEntity> LoadJobAsync(Guid jobId)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            return await dbContext.CleanupJobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId);
        }

        public async Task AssertParseRunRemovedAsync(SeededCleanupJob seeded)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            Assert.False(await dbContext.ParseRuns.AnyAsync(run => run.Id == seeded.ParseRunId));
            // Deleting one Parse Run keeps its Document with the original file.
            Assert.True(await dbContext.Documents.AnyAsync(document => document.Id == seeded.DocumentId));
        }

        public async Task AssertDocumentRemovedAsync(SeededCleanupJob seeded)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            Assert.False(await dbContext.ParseRuns.AnyAsync(run => run.DocumentId == seeded.DocumentId));
            Assert.False(await dbContext.Documents.AnyAsync(document => document.Id == seeded.DocumentId));
        }

        public async Task AssertRowsIntactAsync(SeededCleanupJob seeded)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            Assert.True(await dbContext.ParseRuns.AnyAsync(run => run.Id == seeded.ParseRunId));
            Assert.True(await dbContext.Documents.AnyAsync(document => document.Id == seeded.DocumentId));
        }

        public async ValueTask DisposeAsync()
        {
            await serviceProvider.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
