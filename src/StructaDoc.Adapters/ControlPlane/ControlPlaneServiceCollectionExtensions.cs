using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Audit;
using ServiceMantle.Persistence.Relational.Stores;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Adapters.ControlPlane;

public static class ControlPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocControlPlane(
        this IServiceCollection services,
        ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        services.AddSingleton(options);
        services.AddDbContext<ControlPlaneDbContext>(
            builder => ConfigureControlPlane(builder, options));
        // The management audit writer stages rows on the same scoped control-plane context as the
        // record point's own changes, so a point that saves through that context commits its audit
        // row and its change in one unit of work.
        services.AddScoped<IManagementAuditWriter>(serviceProvider =>
            new EfCoreManagementAuditWriter<ControlPlaneDbContext>(
                serviceProvider.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<StructaDocManagementAuditRecorder>();
        // The read side of the same table: one query service per request scope, reading through
        // the request's own control-plane context. It validates queries, re-checks legacy rows,
        // and never writes.
        services.AddScoped<IManagementAuditQueryService>(serviceProvider =>
            new EfCoreManagementAuditQueryService<ControlPlaneDbContext>(
                serviceProvider.GetRequiredService<ControlPlaneDbContext>()));

        return services;
    }

    public static void ConfigureControlPlane(
        DbContextOptionsBuilder optionsBuilder,
        ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        optionsBuilder.UseSqlite(
            options.ConnectionString,
            sqlite => sqlite.MigrationsAssembly(DatabaseMigrationAssemblies.Sqlite));
    }

    public static async Task ApplyStructaDocControlPlaneMigrationsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<ControlPlaneOptions>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
