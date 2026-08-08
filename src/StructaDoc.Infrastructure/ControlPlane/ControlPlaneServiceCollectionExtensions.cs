using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Infrastructure.Persistence;

namespace StructaDoc.Infrastructure.ControlPlane;

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
        services
            .AddHealthChecks()
            .AddDbContextCheck<ControlPlaneDbContext>("control-plane", tags: ["ready"]);

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
