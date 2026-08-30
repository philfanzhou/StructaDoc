using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.DatabaseContractTests;

internal static class InnoDbMigrationPreflightContract
{
    public static async Task AssertAsync(
        DatabaseProvider provider,
        string connectionString,
        string serverVersion)
    {
        Assert.True(provider is DatabaseProvider.MySql or DatabaseProvider.MariaDb);

        var options = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
            ServerVersion = serverVersion,
            ApplyMigrationsOnStartup = true,
        };
        var connectionBuilder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = connectionBuilder.Database;
        Assert.Matches("^[A-Za-z0-9_]+$", databaseName);

        var preflight = new InnoDbMigrationPreflight();
        await DropDatabaseAsync(connectionBuilder, databaseName);

        // The server connection must establish absence before anything asks the qualified
        // connection for migration history. The complete migration set is pending in this state.
        var absent = await preflight.CheckAsync(options);
        Assert.False(absent.DatabaseExists);
        Assert.True(absent.RequiresInnoDbValidation);
        Assert.Contains(
            absent.PendingMigrations,
            migration => migration.EndsWith(
                "_AddProviderConfigsAndParseCreation",
                StringComparison.Ordinal));

        await CreateDatabaseAsync(connectionBuilder, databaseName);

        // An existing empty database has no history table. It is still treated as having the full
        // migration set pending, and the absent-table branch consults the DYNAMIC server default.
        var missingHistory = await preflight.CheckAsync(options);
        Assert.True(missingHistory.DatabaseExists);
        Assert.True(missingHistory.RequiresInnoDbValidation);

        var previousMigration = provider == DatabaseProvider.MySql
            ? "20260805091104_AddAuthentication"
            : "20260805091111_AddAuthentication";
        await MigrateAsync(options, previousMigration);

        // Existing affected tables use their actual row format, not the current default.
        await ExecuteInDatabaseAsync(
            connectionBuilder,
            "ALTER TABLE `parse_runs` ROW_FORMAT=COMPACT");
        var compactError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => preflight.CheckAsync(options));
        Assert.Contains("ROW_FORMAT=Compact", compactError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE", compactError.Message, StringComparison.Ordinal);

        await ExecuteInDatabaseAsync(
            connectionBuilder,
            "ALTER TABLE `parse_runs` ROW_FORMAT=DYNAMIC");
        var originalDefault = await ReadGlobalDefaultRowFormatAsync(connectionBuilder);
        try
        {
            await SetGlobalDefaultRowFormatAsync(connectionBuilder, "COMPACT");
            var dynamic = await preflight.CheckAsync(options);
            Assert.True(dynamic.RequiresInnoDbValidation);
        }
        finally
        {
            await SetGlobalDefaultRowFormatAsync(connectionBuilder, originalDefault);
        }

        await MigrateAsync(options);
        var previousDefault = await ReadGlobalDefaultRowFormatAsync(connectionBuilder);
        try
        {
            await SetGlobalDefaultRowFormatAsync(connectionBuilder, "COMPACT");

            // Once every registered migration is applied, a later default change is irrelevant.
            var current = await preflight.CheckAsync(options);
            Assert.False(current.RequiresInnoDbValidation);
            Assert.Empty(current.PendingMigrations);

            // A future table in an absent database does depend on the server default and must fail
            // before EF Core can create the database or emit a raw key-too-long diagnostic.
            await DropDatabaseAsync(connectionBuilder, databaseName);
            var defaultError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => preflight.CheckAsync(options));
            Assert.Contains(
                "innodb_default_row_format=COMPACT",
                defaultError.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await SetGlobalDefaultRowFormatAsync(connectionBuilder, previousDefault);
        }

        // Leave the container's configured database current for the ordinary database contract that
        // runs after this preflight contract in the same container.
        await MigrateAsync(options);
    }

    private static async Task MigrateAsync(
        DatabaseOptions options,
        string? targetMigration = null)
    {
        var builder = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(builder, options);
        await using var context = new StructaDocDbContext(builder.Options);
        if (targetMigration is null)
        {
            await context.Database.MigrateAsync();
            return;
        }

        await context.Database.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task DropDatabaseAsync(
        MySqlConnectionStringBuilder builder,
        string databaseName)
    {
        await using var connection = await OpenServerConnectionAsync(builder);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateDatabaseAsync(
        MySqlConnectionStringBuilder builder,
        string databaseName)
    {
        await using var connection = await OpenServerConnectionAsync(builder);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteInDatabaseAsync(
        MySqlConnectionStringBuilder builder,
        string commandText)
    {
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadGlobalDefaultRowFormatAsync(
        MySqlConnectionStringBuilder builder)
    {
        await using var connection = await OpenServerConnectionAsync(builder);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@GLOBAL.innodb_default_row_format";
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetGlobalDefaultRowFormatAsync(
        MySqlConnectionStringBuilder builder,
        string rowFormat)
    {
        Assert.True(rowFormat.Equals("DYNAMIC", StringComparison.OrdinalIgnoreCase)
            || rowFormat.Equals("COMPACT", StringComparison.OrdinalIgnoreCase));
        await using var connection = await OpenServerConnectionAsync(builder);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET GLOBAL innodb_default_row_format = '{rowFormat}'";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<MySqlConnection> OpenServerConnectionAsync(
        MySqlConnectionStringBuilder source)
    {
        var builder = new MySqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = string.Empty,
        };
        var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
