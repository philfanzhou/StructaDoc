using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Persistence.Tests;

/// <summary>
/// The ServiceMantle migration orchestration on the SQLite business database: the single-instance
/// deployment mode serializes concurrent sessions in this process so the schema applies exactly
/// once, and a multi-instance request is rejected by the deployment validator before any side
/// effect reaches the filesystem.
/// </summary>
public sealed class ServiceMantleMigrationOrchestrationTests
{
    [Fact]
    public async Task Concurrent_single_instance_sessions_apply_sqlite_migrations_exactly_once()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-orchestration-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var databaseOptions = new DatabaseOptions
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = $"Data Source={Path.Combine(directory, "business.db")};Pooling=False",
                ApplyMigrationsOnStartup = true,
            };
            using var deployment = new OrchestrationDeployment(
                Path.Combine(Path.GetTempPath(), $"control-{Guid.NewGuid():N}.db"),
                databaseOptions);
            await deployment.MigrateControlPlaneAsync(TestContext.Current.CancellationToken);

            // Two sessions start at the same time on the one process the single-instance
            // constraint allows. The process-local turn must let exactly one apply the schema;
            // the other waits, re-inspects, and passes without executing.
            var first = deployment.OrchestrateAsync(
                databaseOptions,
                TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);
            var second = deployment.OrchestrateAsync(
                databaseOptions,
                TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);
            var results = await Task.WhenAll(first, second);

            Assert.All(results, result => Assert.True(result.Succeeded, result.ToString()));
            Assert.Equal(1, results.Count(result => result.ExecutorWasCalled));

            var builder = new DbContextOptionsBuilder<StructaDocDbContext>();
            PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, databaseOptions);
            await using var context = new StructaDocDbContext(builder.Options);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Multi_instance_sqlite_request_is_rejected_without_side_effects()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-orchestration-tests",
            Guid.NewGuid().ToString("N"));
        // Deliberately not created: an untouched tree is what proves the rejected request made no
        // side effect at all.
        var databaseOptions = new DatabaseOptions
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = $"Data Source={Path.Combine(directory, "nested", "business.db")};Pooling=False",
            ApplyMigrationsOnStartup = true,
        };
        using var deployment = new OrchestrationDeployment(
            Path.Combine(Directory.GetCurrentDirectory(), $"control-{Guid.NewGuid():N}.db"),
            databaseOptions);

        var result = await deployment.OrchestrateAsync(
            databaseOptions,
            mode: DatabaseDeploymentMode.MultiInstance,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Relative_sqlite_data_source_still_resolves_a_single_instance_target()
    {
        // The shipped development default is a relative data source; the bootstrap copy is
        // normalized against the working directory so the single-instance turn still applies.
        var workingDirectory = Directory.GetCurrentDirectory();
        var databaseFileName = $"relative-{Guid.NewGuid():N}.db";
        var databaseOptions = new DatabaseOptions
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = $"Data Source={databaseFileName};Pooling=False",
            ApplyMigrationsOnStartup = true,
        };

        try
        {
            using var deployment = new OrchestrationDeployment(
                Path.Combine(workingDirectory, $"control-{Guid.NewGuid():N}.db"),
                databaseOptions);
            await deployment.MigrateControlPlaneAsync(TestContext.Current.CancellationToken);

            var result = await deployment.OrchestrateAsync(
                databaseOptions,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.ToString());
            Assert.True(result.ExecutorWasCalled);
            Assert.True(File.Exists(Path.Combine(workingDirectory, databaseFileName)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(Path.Combine(workingDirectory, databaseFileName));
        }
    }

    [Fact]
    public async Task Symbolic_link_directory_and_connection_parameters_target_the_same_physical_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"structadoc-link-{Guid.NewGuid():N}");
        var physical = Path.Combine(directory, "physical");
        var alias = Path.Combine(directory, "alias");
        Directory.CreateDirectory(physical);
        Directory.CreateSymbolicLink(alias, physical);
        var options = new DatabaseOptions
        {
            ConnectionString = $"Data Source={alias}/business.db;Pooling=False;Default Timeout=7",
        };
        try
        {
            var bootstrap = ServiceMantleMigrationOrchestration.ToBootstrapDatabaseConfiguration(options);
            var parsed = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(bootstrap.ConnectionString);
            Assert.False(parsed.Pooling);
            Assert.Equal(7, parsed.DefaultTimeout);
            Assert.DoesNotContain("/alias/", parsed.DataSource, StringComparison.Ordinal);
            using var deployment = new OrchestrationDeployment(Path.Combine(directory, "control.db"), options);
            await deployment.MigrateControlPlaneAsync(TestContext.Current.CancellationToken);
            var result = await deployment.OrchestrateAsync(options, TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.ToString());
            Assert.True(File.Exists(Path.Combine(physical, "business.db")));
            var repeated = await deployment.OrchestrateAsync(options, TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.True(repeated.Succeeded);
            Assert.False(repeated.ExecutorWasCalled);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class OrchestrationDeployment : IDisposable
    {
        private readonly ServiceProvider serviceProvider;
        private readonly string controlPlanePath;

        public OrchestrationDeployment(string controlPlanePath, DatabaseOptions databaseOptions)
        {
            this.controlPlanePath = controlPlanePath;
            var controlPlaneOptions = new ControlPlaneOptions
            {
                DatabasePath = controlPlanePath,
            };
            var services = new ServiceCollection();
            services.AddStructaDocControlPlane(controlPlaneOptions);
            services.AddStructaDocPersistenceMigrationServices(databaseOptions);
            serviceProvider = services.BuildServiceProvider();
        }

        public async Task MigrateControlPlaneAsync(CancellationToken cancellationToken) =>
            await serviceProvider.ApplyStructaDocControlPlaneMigrationsAsync(cancellationToken);

        public Task<StartupDatabaseGateResult> OrchestrateAsync(
            DatabaseOptions databaseOptions,
            TimeSpan lockAcquireTimeout,
            CancellationToken cancellationToken) =>
            OrchestrateAsync(
                databaseOptions,
                ServiceMantleMigrationOrchestration.ResolveDeploymentMode(databaseOptions.Provider),
                lockAcquireTimeout,
                cancellationToken);

        public Task<StartupDatabaseGateResult> OrchestrateAsync(
            DatabaseOptions databaseOptions,
            DatabaseDeploymentMode mode,
            TimeSpan lockAcquireTimeout,
            CancellationToken cancellationToken) =>
            serviceProvider.OrchestrateStructaDocMigrationsAsync(
                databaseOptions,
                mode,
                lockAcquireTimeout,
                cancellationToken);

        public void Dispose()
        {
            serviceProvider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(controlPlanePath))
            {
                File.Delete(controlPlanePath);
            }
        }
    }
}
