using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb.Migration;
using ServiceMantle.Database.MySql.Migration;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Database.Sqlite;
using ServiceMantle.Migration;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Adapters.Persistence.Providers;
using StructaDoc.Adapters.Resources;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Resources;

namespace StructaDoc.Adapters.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocPersistence(
        this IServiceCollection services,
        DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        services.AddStructaDocPersistenceMigrationServices(databaseOptions);
        services.AddScoped<IParseRunLeaseStore, EfCoreParseRunLeaseStore>();
        services.AddScoped<IParseRunStateStore, EfCoreParseRunStateStore>();
        services.AddScoped<IParseRunConversionStore, EfCoreParseRunConversionStore>();
        services.AddScoped<IParseRunSubmissionCheckpointStore, EfCoreParseRunSubmissionCheckpointStore>();
        services.AddScoped<IParseSegmentMutationStore, EfCoreParseSegmentMutationStore>();
        services.AddScoped<IParseRunExecutionContextStore, EfCoreParseRunExecutionContextStore>();
        services.AddScoped<IParseBundleCommitStore, EfCoreParseBundleCommitStore>();
        services.AddScoped<IParseRunService, EfCoreParseRunService>();
        services.AddScoped<IParseResultReadService, EfCoreParseResultReadService>();
        services.AddScoped<IResourceDeletionService, EfCoreResourceDeletionService>();
        services.AddScoped<IProviderConfigAdministrationService, EfCoreProviderConfigAdministrationService>();
        services.AddScoped<IParseProviderResolver, ParseProviderResolver>();

        return services;
    }

    /// <summary>
    /// Registers only the business database services needed to inspect and migrate its schema. The
    /// one-shot migration command deliberately does not construct runtime stores, health checks, or
    /// any Worker dependency graph.
    /// </summary>
    public static IServiceCollection AddStructaDocPersistenceMigrationServices(
        this IServiceCollection services,
        DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        databaseOptions.Validate();

        services.AddSingleton(databaseOptions);
        services.AddSingleton<IBusinessDatabaseMigrationPreflight, InnoDbMigrationPreflight>();
        // The options are deliberately singleton-lifetime: the Data Protection key ring's database
        // form resolves IDbContextFactory<StructaDocDbContext> as a singleton, and a scoped options
        // registration cannot feed one. The factory's contexts create and commit independently of
        // any caller's unit of work, so it never joins a scoped context's transaction.
        services.AddDbContext<StructaDocDbContext>(
            options => ConfigureDatabase(options, databaseOptions),
            contextLifetime: ServiceLifetime.Scoped,
            optionsLifetime: ServiceLifetime.Singleton);
        services.AddDbContextFactory<StructaDocDbContext>(
            options => ConfigureDatabase(options, databaseOptions));
        // The executor logs through the standard logging abstraction, and the migration command's
        // builder already provides it. Registering it here keeps the composition usable on a bare
        // service collection as well; AddLogging is idempotent in a host.
        services.AddLogging();
        // ServiceMantle orchestration: the executor carries the three-step migration workflow, the
        // registries carry provider leases and deployment declarations. The direct gate creates
        // an executor scope for each session, so a session's context and lease never outlive it.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseMigrationLockProvider,
            PostgreSqlMigrationLockProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseMigrationLockProvider,
            MySqlMigrationLockProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseMigrationLockProvider,
            MariaDbMigrationLockProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseDeploymentCapabilityProvider,
            SqliteDatabaseTargetPreparationProvider>());
        services.AddServiceMantlePostgreSqlDeploymentCapability();
        services.AddServiceMantleMySqlDeploymentCapability();
        services.AddServiceMantleMariaDbDeploymentCapability();
        services.AddScoped<IDatabaseMigrationExecutor, StructaDocMigrationExecutor>();
        // Direct invocation preserves the Host's stored-settings recovery boundary. The
        // shared entry registers no hosted runner and performs no target preparation.
        services.AddServiceMantleStartupDatabaseGateServices();

        return services;
    }

    public static void ConfigureDatabase(
        DbContextOptionsBuilder optionsBuilder,
        DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        databaseOptions.Validate();

        switch (databaseOptions.Provider)
        {
            case DatabaseProvider.Sqlite:
                optionsBuilder.UseSqlite(
                    databaseOptions.ConnectionString,
                    sqlite => sqlite.MigrationsAssembly(DatabaseMigrationAssemblies.Sqlite));
                break;

            case DatabaseProvider.PostgreSql:
                optionsBuilder.UseNpgsql(
                    databaseOptions.ConnectionString,
                    postgreSql => postgreSql
                        .MigrationsAssembly(DatabaseMigrationAssemblies.PostgreSql)
                        .EnableRetryOnFailure());
                break;

            case DatabaseProvider.MySql:
                optionsBuilder.UseMySql(
                    databaseOptions.ConnectionString,
                    new MySqlServerVersion(Version.Parse(databaseOptions.ServerVersion!)),
                    mySql => mySql
                        .MigrationsAssembly(DatabaseMigrationAssemblies.MySql)
                        .EnableRetryOnFailure());
                break;

            case DatabaseProvider.MariaDb:
                optionsBuilder.UseMySql(
                    databaseOptions.ConnectionString,
                    new MariaDbServerVersion(Version.Parse(databaseOptions.ServerVersion!)),
                    mariaDb => mariaDb
                        .MigrationsAssembly(DatabaseMigrationAssemblies.MariaDb)
                        .EnableRetryOnFailure());
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider '{databaseOptions.Provider}'.");
        }
    }
}
