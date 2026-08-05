using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Infrastructure.Persistence.ParseRuns;
using StructaDoc.Infrastructure.Persistence.Providers;

namespace StructaDoc.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocPersistence(
        this IServiceCollection services,
        DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        databaseOptions.Validate();

        services.AddSingleton(databaseOptions);
        services.AddDbContext<StructaDocDbContext>(
            options => ConfigureDatabase(options, databaseOptions));
        services.AddScoped<IParseRunLeaseStore, EfCoreParseRunLeaseStore>();
        services.AddScoped<IParseRunStateStore, EfCoreParseRunStateStore>();
        services.AddScoped<IParseRunConversionStore, EfCoreParseRunConversionStore>();
        services.AddScoped<IParseRunSubmissionCheckpointStore, EfCoreParseRunSubmissionCheckpointStore>();
        services.AddScoped<IParseRunExecutionContextStore, EfCoreParseRunExecutionContextStore>();
        services.AddScoped<IParseBundleCommitStore, EfCoreParseBundleCommitStore>();
        services.AddScoped<IParseRunService, EfCoreParseRunService>();
        services.AddScoped<IProviderConfigAdministrationService, EfCoreProviderConfigAdministrationService>();
        services.AddScoped<IParseProviderResolver, ParseProviderResolver>();
        services
            .AddHealthChecks()
            .AddDbContextCheck<StructaDocDbContext>(
                "database",
                tags: ["ready"]);

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
