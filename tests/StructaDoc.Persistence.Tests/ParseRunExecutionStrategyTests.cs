using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Domain.Resources;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

public sealed class ParseRunExecutionStrategyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("request-1")]
    public async Task Replayed_execution_strategy_delegate_creates_one_parse_run(
        string? idempotencyKey)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "execution-strategy.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var standardOptions = CreateOptions(connectionString);
        var documentId = Guid.NewGuid();
        var providerConfigId = Guid.NewGuid();
        var providerVersionId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;

        try
        {
            await using (var setupContext = new StructaDocDbContext(standardOptions))
            {
                await setupContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
                setupContext.Documents.Add(new DocumentEntity
                {
                    Id = documentId,
                    OriginalFileName = "execution-strategy.pdf",
                    MediaType = "application/pdf",
                    Extension = ".pdf",
                    SizeBytes = 128,
                    Sha256 = new string('a', 64),
                    StorageRef = "documents/execution-strategy.pdf",
                    LifecycleState = ResourceLifecycleStates.Active,
                    CreatedAtUtc = nowUtc,
                });
                var provider = new ProviderConfigEntity
                {
                    Id = providerConfigId,
                    Name = "Execution Strategy Provider",
                    ProviderType = "mineru-local",
                    IsEnabled = true,
                    DefaultMarker = "default",
                    CurrentVersionId = providerVersionId,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                };
                setupContext.ProviderConfigs.Add(provider);
                setupContext.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
                {
                    Id = providerVersionId,
                    ProviderConfigId = providerConfigId,
                    ProviderConfig = provider,
                    VersionNumber = 1,
                    BaseUrl = "http://provider.test/",
                    CreatedAtUtc = nowUtc,
                });
                await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var replayingOptions = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    connectionString,
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly))
                .ReplaceService<IExecutionStrategyFactory, ReplayingExecutionStrategyFactory>()
                .Options;

            ParseRunCreationResult result;
            await using (var creationContext = new StructaDocDbContext(replayingOptions))
            {
                result = await new EfCoreParseRunService(creationContext).CreateAsync(
                    new ParseRunCreateRequest(
                        documentId,
                        providerConfigId,
                        "{}",
                        3,
                        "test-actor",
                        idempotencyKey,
                        nowUtc),
                    TestContext.Current.CancellationToken);
            }

            await using var verificationContext = new StructaDocDbContext(standardOptions);
            var parseRun = Assert.Single(await verificationContext.ParseRuns.AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken));

            Assert.Equal(ParseRunCreationStatus.Created, result.Status);
            Assert.Equal(parseRun.Id, result.ParseRun!.Id);
            Assert.Equal(1, await verificationContext.Documents
                .Select(document => document.ConcurrencyVersion)
                .SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await verificationContext.ProviderConfigs
                .Select(provider => provider.ConcurrencyVersion)
                .SingleAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DbContextOptions<StructaDocDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<StructaDocDbContext>()
            .UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly(
                    typeof(SqliteDesignTimeDbContextFactory).Assembly))
            .Options;

    private sealed class ReplayingExecutionStrategyFactory(
        ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() =>
            new ReplayingExecutionStrategy(dependencies.CurrentContext.Context);
    }

    private sealed class ReplayingExecutionStrategy(DbContext context) : IExecutionStrategy
    {
        private static readonly AsyncLocal<int> ExecutionDepth = new();

        public bool RetriesOnFailure => true;

        public TResult Execute<TState, TResult>(
            TState state,
            Func<DbContext, TState, TResult> operation,
            Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded)
        {
            if (ExecutionDepth.Value > 0)
            {
                return operation(context, state);
            }

            ExecutionDepth.Value++;
            try
            {
                _ = operation(context, state);
                return operation(context, state);
            }
            finally
            {
                ExecutionDepth.Value--;
            }
        }

        public async Task<TResult> ExecuteAsync<TState, TResult>(
            TState state,
            Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>?
                verifySucceeded,
            CancellationToken cancellationToken = default)
        {
            if (ExecutionDepth.Value > 0)
            {
                return await operation(context, state, cancellationToken);
            }

            ExecutionDepth.Value++;
            try
            {
                _ = await operation(context, state, cancellationToken);
                return await operation(context, state, cancellationToken);
            }
            finally
            {
                ExecutionDepth.Value--;
            }
        }
    }
}
