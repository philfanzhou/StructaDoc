using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace StructaDoc.Platform.Persistence;

public static class DatabaseMigrationExtensions
{
    public static async Task ApplyStructaDocMigrationsAsync(
        this IServiceProvider serviceProvider,
        DatabaseOptions databaseOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        if (!databaseOptions.ApplyMigrationsOnStartup)
        {
            return;
        }

        if (databaseOptions.Provider == DatabaseProvider.Sqlite)
        {
            EnsureSqliteDirectoryExists(databaseOptions.ConnectionString);
        }

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    private static void EnsureSqliteDirectoryExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;

        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
