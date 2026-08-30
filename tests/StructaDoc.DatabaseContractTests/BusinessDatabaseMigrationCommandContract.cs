using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Host.Migrations;

namespace StructaDoc.DatabaseContractTests;

internal static class BusinessDatabaseMigrationCommandContract
{
    public static async Task AssertAsync(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-migration-command-contracts",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var arguments = CreateArguments(
                provider,
                connectionString,
                serverVersion,
                Path.Combine(directory, "control.db"));

            Assert.Equal(
                0,
                await BusinessDatabaseMigrationCommand.ExecuteAsync(
                    arguments,
                    cancellationToken));
            Assert.Equal(
                0,
                await BusinessDatabaseMigrationCommand.ExecuteAsync(
                    arguments,
                    cancellationToken));

            var databaseOptions = new DatabaseOptions
            {
                Provider = provider,
                ConnectionString = connectionString,
                ServerVersion = serverVersion,
                ApplyMigrationsOnStartup = false,
            };
            var builder = new DbContextOptionsBuilder<StructaDocDbContext>();
            PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, databaseOptions);
            await using var context = new StructaDocDbContext(builder.Options);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task<int> ExecuteAsync(
        DatabaseProvider provider,
        string connectionString,
        string serverVersion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-migration-command-contracts",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            return await BusinessDatabaseMigrationCommand.ExecuteAsync(
                CreateArguments(
                    provider,
                    connectionString,
                    serverVersion,
                    Path.Combine(directory, "control.db")),
                cancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] CreateArguments(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion,
        string controlPlanePath)
    {
        var arguments = new List<string>
        {
            $"--ControlPlane:DatabasePath={controlPlanePath}",
            $"--Database:Provider={provider}",
            $"--Database:ConnectionString={connectionString}",
            "--Database:ApplyMigrationsOnStartup=false",
        };
        if (serverVersion is not null)
        {
            arguments.Add($"--Database:ServerVersion={serverVersion}");
        }

        return [.. arguments];
    }
}
