using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Migration;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.DatabaseContractTests;

/// <summary>
/// The ServiceMantle migration orchestration contract on real server databases: concurrent
/// orchestration sessions apply the schema exactly once, the lease times out with a stable error
/// code while another session holds it, and a database newer than the application fails closed
/// before any executor runs.
/// </summary>
internal static class MigrationOrchestrationContract
{
    public static async Task AssertAsync(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databaseOptions = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
            ServerVersion = serverVersion,
            ApplyMigrationsOnStartup = true,
        };

        using var deployment = new OrchestrationDeployment(provider, databaseOptions);
        await deployment.MigrateControlPlaneAsync(cancellationToken);

        await AssertConcurrentSessionsApplyExactlyOnceAsync(
            deployment,
            databaseOptions,
            cancellationToken);
        await AssertVersionTooNewFailsClosedAsync(
            deployment,
            databaseOptions,
            cancellationToken);
        await AssertLockTimesOutWhileAnotherSessionHoldsItAsync(
            deployment,
            databaseOptions,
            cancellationToken);
    }

    private static async Task AssertConcurrentSessionsApplyExactlyOnceAsync(
        OrchestrationDeployment deployment,
        DatabaseOptions databaseOptions,
        CancellationToken cancellationToken)
    {
        // Two sessions start against the same fresh database at the same time, which is what two
        // replicas pulled up together do. The lease must let exactly one apply the schema; the
        // other waits and passes without executing.
        var first = deployment.OrchestrateAsync(
            databaseOptions,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        var second = deployment.OrchestrateAsync(
            databaseOptions,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded, result.ToString()));
        Assert.Equal(
            1,
            results.Count(result => result.ExecutorWasCalled));

        // The waiting session's re-inspection is what proved compatibility, and the applied
        // history matches the application's migration list exactly: nothing applied twice and
        // nothing unknown.
        var defined = await ReadMigrationListAsync(databaseOptions, defined: true, cancellationToken);
        var applied = await ReadMigrationListAsync(databaseOptions, defined: false, cancellationToken);
        Assert.Empty(defined.Except(applied, StringComparer.Ordinal));
        Assert.Equal(applied.Length, defined.Length);
    }

    private static async Task AssertVersionTooNewFailsClosedAsync(
        OrchestrationDeployment deployment,
        DatabaseOptions databaseOptions,
        CancellationToken cancellationToken)
    {
        await InsertHistoryRowAsync(
            databaseOptions,
            "20990101000000_FromTheFuture",
            cancellationToken);

        try
        {
            var result = await deployment.OrchestrateAsync(
                databaseOptions,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            Assert.False(result.Succeeded);
            Assert.Equal(WellKnownMigrationErrorCodes.VersionTooNew, result.ErrorCode);
            Assert.False(result.ExecutorWasCalled);
        }
        finally
        {
            await DeleteHistoryRowAsync(
                databaseOptions,
                "20990101000000_FromTheFuture",
                cancellationToken);
        }

        // The fail-closed decision left the database untouched, so the ordinary session still
        // succeeds without executing anything.
        var recovered = await deployment.OrchestrateAsync(
            databaseOptions,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.True(recovered.Succeeded, recovered.ToString());
        Assert.False(recovered.ExecutorWasCalled);
    }

    private static async Task AssertLockTimesOutWhileAnotherSessionHoldsItAsync(
        OrchestrationDeployment deployment,
        DatabaseOptions databaseOptions,
        CancellationToken cancellationToken)
    {
        var lockProviderRegistry = ServiceMantleMigrationOrchestration.CreateMigrationLockProviderRegistry();
        var blocking = new BlockingMigrationExecutor(TimeSpan.FromSeconds(8));
        var holder = new DatabaseMigrationOrchestrator(blocking, lockProviderRegistry);
        var bootstrap = ServiceMantleMigrationOrchestration.ToBootstrapDatabaseConfiguration(databaseOptions);

        var holding = holder.OrchestrateMigrationAsync(
            ServiceMantleMigrationOrchestration.MigrationServiceId,
            bootstrap,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        await blocking.Started.WaitAsync(cancellationToken);

        var result = await deployment.OrchestrateAsync(
            databaseOptions,
            TimeSpan.FromSeconds(2),
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockTimeout, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);

        // The holder's own session ends and releases the lease, whatever its final state is.
        await holding;
    }

    private static async Task<string[]> ReadMigrationListAsync(
        DatabaseOptions databaseOptions,
        bool defined,
        CancellationToken cancellationToken)
    {
        var builder = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, databaseOptions);
        await using var context = new StructaDocDbContext(builder.Options);
        return defined
            ? context.Database.GetMigrations().ToArray()
            : (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
    }

    private static async Task InsertHistoryRowAsync(
        DatabaseOptions databaseOptions,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var builder = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, databaseOptions);
        await using var context = new StructaDocDbContext(builder.Options);
        // The identifiers are provider-quoting decisions made in this file, and the migration id
        // is a test constant, so the statement is assembled rather than parameterized.
        var sql = "INSERT INTO "
            + HistoryTableName(databaseOptions.Provider)
            + " ("
            + MigrationIdColumn(databaseOptions.Provider)
            + ", "
            + ProductVersionColumn(databaseOptions.Provider)
            + ") VALUES ('"
            + migrationId
            + "', '10.0.11')";
        await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task DeleteHistoryRowAsync(
        DatabaseOptions databaseOptions,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var builder = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, databaseOptions);
        await using var context = new StructaDocDbContext(builder.Options);
        var sql = "DELETE FROM "
            + HistoryTableName(databaseOptions.Provider)
            + " WHERE "
            + MigrationIdColumn(databaseOptions.Provider)
            + " = '"
            + migrationId
            + "'";
        await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static string HistoryTableName(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => "\"__EFMigrationsHistory\"",
        DatabaseProvider.MySql or DatabaseProvider.MariaDb => "__EFMigrationsHistory",
        _ => throw new InvalidOperationException(
            $"The orchestration contract does not quote history identifiers for '{provider}'."),
    };

    private static string MigrationIdColumn(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => "\"MigrationId\"",
        DatabaseProvider.MySql or DatabaseProvider.MariaDb => "MigrationId",
        _ => throw new InvalidOperationException(
            $"The orchestration contract does not quote history identifiers for '{provider}'."),
    };

    private static string ProductVersionColumn(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => "\"ProductVersion\"",
        DatabaseProvider.MySql or DatabaseProvider.MariaDb => "ProductVersion",
        _ => throw new InvalidOperationException(
            $"The orchestration contract does not quote history identifiers for '{provider}'."),
    };

    private sealed class OrchestrationDeployment : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-orchestration-contracts",
            Guid.NewGuid().ToString("N"));

        private readonly ServiceProvider serviceProvider;

        public OrchestrationDeployment(DatabaseProvider provider, DatabaseOptions databaseOptions)
        {
            Directory.CreateDirectory(directory);
            var controlPlaneOptions = new ControlPlaneOptions
            {
                DatabasePath = Path.Combine(directory, "control.db"),
            };
            var services = new ServiceCollection();
            services.AddStructaDocControlPlane(controlPlaneOptions);
            services.AddStructaDocPersistenceMigrationServices(databaseOptions);
            serviceProvider = services.BuildServiceProvider();
        }

        public async Task MigrateControlPlaneAsync(CancellationToken cancellationToken) =>
            await serviceProvider.ApplyStructaDocControlPlaneMigrationsAsync(cancellationToken);

        public Task<MigrationExecutionResult> OrchestrateAsync(
            DatabaseOptions databaseOptions,
            TimeSpan lockAcquireTimeout,
            CancellationToken cancellationToken) =>
            serviceProvider.OrchestrateStructaDocMigrationsAsync(
                databaseOptions,
                ServiceMantleMigrationOrchestration.ResolveDeploymentMode(databaseOptions.Provider),
                lockAcquireTimeout,
                cancellationToken);

        public void Dispose()
        {
            serviceProvider.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Holds the provider lease for a bounded time so another session can be observed timing out.
    /// The lease is what is under test, not the migration stages.
    /// </summary>
    private sealed class BlockingMigrationExecutor(TimeSpan holdDuration) : IDatabaseMigrationExecutor
    {
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MigrationObservationState.Empty);

        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            started.SetResult();
            await Task.Delay(holdDuration, cancellationToken);
        }
    }
}
