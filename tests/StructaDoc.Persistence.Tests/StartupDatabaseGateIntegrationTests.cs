using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Health;
using ServiceMantle.Migration;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Persistence.Tests;

public sealed class StartupDatabaseGateIntegrationTests
{
    private const string Secret = "gate-test-secret";

    [Theory]
    [InlineData(DatabaseProvider.Sqlite, WellKnownDatabaseProviderIds.Sqlite)]
    [InlineData(DatabaseProvider.PostgreSql, WellKnownDatabaseProviderIds.PostgreSql)]
    [InlineData(DatabaseProvider.MySql, WellKnownDatabaseProviderIds.MySql)]
    [InlineData(DatabaseProvider.MariaDb, WellKnownDatabaseProviderIds.MariaDb)]
    public async Task Every_provider_skips_preparation_and_repeated_sessions_use_fresh_receipts(
        DatabaseProvider provider, string providerId)
    {
        var executor = new CountingExecutor();
        var preparation = new ForbiddenPreparation(providerId);
        var lease = new TestLease(providerId);
        var options = Options(provider);
        await using var services = Services(options, executor, preparation, new TestLockProvider(lease));

        var first = await services.OrchestrateStructaDocMigrationsAsync(
            options, ServiceMantleMigrationOrchestration.ResolveDeploymentMode(provider),
            cancellationToken: TestContext.Current.CancellationToken);
        var repeat = await services.OrchestrateStructaDocMigrationsAsync(
            options, ServiceMantleMigrationOrchestration.ResolveDeploymentMode(provider),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded, first.ToString());
        Assert.True(first.ExecutorWasCalled);
        Assert.True(repeat.Succeeded, repeat.ToString());
        Assert.False(repeat.ExecutorWasCalled);
        Assert.Equal(1, executor.Executions);
        Assert.Equal(0, preparation.Calls);
    }

    [Theory]
    [InlineData(DatabaseProvider.PostgreSql, WellKnownDatabaseProviderIds.PostgreSql)]
    [InlineData(DatabaseProvider.MySql, WellKnownDatabaseProviderIds.MySql)]
    [InlineData(DatabaseProvider.MariaDb, WellKnownDatabaseProviderIds.MariaDb)]
    public async Task A_failed_server_lease_has_no_executor_or_preparation_bypass(
        DatabaseProvider provider, string providerId)
    {
        var executor = new CountingExecutor();
        var preparation = new ForbiddenPreparation(providerId);
        var options = Options(provider);
        await using var services = Services(options, executor, preparation,
            new TestLockProvider(new TestLease(providerId), fail: true));

        // Even a caller requesting SingleInstance must take the real server lease.
        var result = await services.OrchestrateStructaDocMigrationsAsync(
            options, DatabaseDeploymentMode.SingleInstance,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
        Assert.Equal(0, executor.Inspections);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(0, preparation.Calls);
        Assert.DoesNotContain(Secret, result.ToString(), StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.ApplyStructaDocMigrationsAsync(options, TestContext.Current.CancellationToken));
        Assert.Contains(WellKnownMigrationErrorCodes.LockFailed, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_before_sqlite_directory_preparation_has_no_side_effects()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"structadoc-cancelled-{Guid.NewGuid():N}");
        var options = new DatabaseOptions { ConnectionString = $"Data Source={directory}/nested/test.db" };
        var executor = new CountingExecutor();
        var preparation = new ForbiddenPreparation(WellKnownDatabaseProviderIds.Sqlite);
        await using var services = Services(options, executor, preparation,
            new TestLockProvider(new TestLease(WellKnownDatabaseProviderIds.Sqlite)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            services.OrchestrateStructaDocMigrationsAsync(options,
                DatabaseDeploymentMode.SingleInstance, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(0, executor.Inspections);
        Assert.Equal(0, preparation.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_lease_loss_during_inspection_stops_later_stages(bool callerCancels)
    {
        using var caller = new CancellationTokenSource();
        using var lost = new CancellationTokenSource();
        var executor = new CountingExecutor(() =>
        {
            if (callerCancels) caller.Cancel();
            else lost.Cancel();
        });
        var options = Options(DatabaseProvider.PostgreSql);
        var preparation = new ForbiddenPreparation(WellKnownDatabaseProviderIds.PostgreSql);
        await using var services = Services(options, executor, preparation,
            new TestLockProvider(new TestLease(WellKnownDatabaseProviderIds.PostgreSql, lost.Token)));

        if (callerCancels)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                services.OrchestrateStructaDocMigrationsAsync(options,
                    DatabaseDeploymentMode.MultiInstance, cancellationToken: caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var result = await services.OrchestrateStructaDocMigrationsAsync(options,
                DatabaseDeploymentMode.MultiInstance, cancellationToken: caller.Token);
            Assert.False(result.Succeeded);
            Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
            Assert.False(result.ExecutorWasCalled);
        }

        Assert.Equal(1, executor.Inspections);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(0, preparation.Calls);
    }

    [Fact]
    public async Task Cancelled_gate_receipt_remains_running_and_startup_switch_skips_the_gate()
    {
        using var caller = new CancellationTokenSource();
        var executor = new CountingExecutor(() => caller.Cancel());
        var options = Options(DatabaseProvider.PostgreSql);
        var preparation = new ForbiddenPreparation(WellKnownDatabaseProviderIds.PostgreSql);
        await using var services = Services(options, executor, preparation,
            new TestLockProvider(new TestLease(WellKnownDatabaseProviderIds.PostgreSql)));
        var receipt = new StartupDatabaseReceipt();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await services.GetRequiredService<StartupDatabaseGate>().RunAsync(
                new StartupDatabaseGateOptions(
                    ServiceMantleMigrationOrchestration.ToBootstrapDatabaseConfiguration(options),
                    DatabaseDeploymentMode.MultiInstance, TimeSpan.FromSeconds(30)),
                receipt, ServiceMantleMigrationOrchestration.MigrationServiceId, caller.Token));
        Assert.Equal(ServiceMigrationReadinessState.Running, receipt.State);
        Assert.Null(receipt.ErrorCode);
        Assert.Equal(0, executor.Executions);

        await services.ApplyStructaDocMigrationsAsync(new DatabaseOptions
        {
            Provider = options.Provider,
            ConnectionString = options.ConnectionString,
            ApplyMigrationsOnStartup = false,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(1, executor.Inspections);
        Assert.Equal(0, preparation.Calls);
    }

    [Theory]
    [InlineData("Data Source=;Password=gate-test-secret")]
    [InlineData("gate-test-secret=invalid")]
    [InlineData("Data Source=|DataDirectory|/test.db;Password=gate-test-secret")]
    public void Invalid_sqlite_resolution_never_echoes_submitted_secrets(string connectionString)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ServiceMantleMigrationOrchestration.ToBootstrapDatabaseConfiguration(
                new DatabaseOptions { ConnectionString = connectionString }));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("file:test.db")]
    public void Special_sqlite_sources_preserve_the_existing_support_boundary(string dataSource)
    {
        var connectionString = $"Data Source={dataSource};Pooling=False";
        var bootstrap = ServiceMantleMigrationOrchestration.ToBootstrapDatabaseConfiguration(
            new DatabaseOptions { ConnectionString = connectionString });
        Assert.Equal(connectionString, bootstrap.ConnectionString);
    }

    private static DatabaseOptions Options(DatabaseProvider provider) => new()
    {
        Provider = provider,
        ConnectionString = provider == DatabaseProvider.Sqlite
            ? $"Data Source={Path.GetTempPath()}/gate-{Guid.NewGuid():N}.db;Pooling=False"
            : $"Host=example.test;Database=structadoc;Password={Secret}",
        ServerVersion = "11.4.0",
    };

    private static ServiceProvider Services(DatabaseOptions options, CountingExecutor executor,
        ForbiddenPreparation preparation, TestLockProvider lockProvider)
    {
        var services = new ServiceCollection();
        services.AddStructaDocPersistenceMigrationServices(options);
        services.AddScoped<IDatabaseMigrationExecutor>(_ => executor);
        services.AddSingleton(new DatabaseTargetPreparationProviderRegistry(
            [preparation], DatabaseProviderIdResolver.Empty));
        services.AddSingleton(new DatabaseMigrationLockProviderRegistry(
            [lockProvider], DatabaseProviderIdResolver.Empty));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class CountingExecutor(Action? inspect = null) : IDatabaseMigrationExecutor
    {
        public int Inspections { get; private set; }
        public int Executions { get; private set; }
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            Inspections++;
            inspect?.Invoke();
            return ValueTask.FromResult(Executions == 0 ? MigrationObservationState.Empty
                : MigrationObservationState.CurrentVersionCompatible);
        }
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Executions++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ForbiddenPreparation(string providerId) : IDatabaseTargetPreparationProvider
    {
        public string ProviderId => providerId;
        public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;
        public int Calls { get; private set; }
        public ValueTask<DatabaseTargetObservation> ObserveAsync(BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Preparation must be disabled.");
        }
        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(DatabaseTargetPreparationRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Creation must be disabled.");
        }
    }

    private sealed class TestLockProvider(TestLease lease, bool fail = false) : IDatabaseMigrationLockProvider
    {
        public string ProviderId => lease.ProviderId;
        public ValueTask<IDatabaseMigrationLock> AcquireAsync(ServiceId serviceId,
            BootstrapDatabaseConfiguration bootstrap, TimeSpan acquireTimeout,
            CancellationToken cancellationToken = default)
        {
            if (fail) throw new InvalidOperationException(Secret);
            return ValueTask.FromResult<IDatabaseMigrationLock>(lease);
        }
    }

    private sealed class TestLease(string providerId, CancellationToken lost = default) : IDatabaseMigrationLock
    {
        public string ProviderId => providerId;
        public CancellationToken LeaseLost => lost;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
