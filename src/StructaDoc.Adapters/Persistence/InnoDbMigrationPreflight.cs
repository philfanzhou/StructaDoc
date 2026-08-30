using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace StructaDoc.Adapters.Persistence;

/// <summary>
/// One migration/index boundary that requires InnoDB's 3072-byte index-key limit.
/// Migration names use a suffix because MySQL and MariaDB migrations have different timestamps.
/// </summary>
public sealed record InnoDbIndexMigrationRequirement(
    string MigrationSuffix,
    string TableName,
    string IndexName);

/// <summary>
/// The single registry shared by application-managed and external migration entry points.
/// Actor migrations add their requirements here when they begin creating or rebuilding indexes.
/// </summary>
public static class InnoDbIndexMigrationRegistry
{
    public static IReadOnlyList<InnoDbIndexMigrationRequirement> Requirements { get; } =
    [
        new(
            "_AddProviderConfigsAndParseCreation",
            "parse_runs",
            "ux_parse_runs_idempotency"),
    ];

    public static IReadOnlyList<InnoDbIndexMigrationRequirement> FindPendingRequirements(
        IEnumerable<string> pendingMigrations)
    {
        ArgumentNullException.ThrowIfNull(pendingMigrations);

        var pending = pendingMigrations.ToArray();
        return Requirements
            .Where(requirement => pending.Any(migration => migration.EndsWith(
                requirement.MigrationSuffix,
                StringComparison.Ordinal)))
            .ToArray();
    }
}

/// <summary>
/// Facts established without opening a database-qualified connection before the target database
/// is known to exist. <see cref="DatabaseExists"/> is meaningful for MySQL and MariaDB; other
/// providers do not need this InnoDB-specific preflight and report <see langword="true"/>.
/// </summary>
public sealed record BusinessDatabaseMigrationPreflightResult(
    bool DatabaseExists,
    IReadOnlyList<string> PendingMigrations,
    IReadOnlyList<InnoDbIndexMigrationRequirement> PendingRequirements)
{
    public bool RequiresInnoDbValidation => PendingRequirements.Count > 0;
}

public interface IBusinessDatabaseMigrationPreflight
{
    Task<BusinessDatabaseMigrationPreflightResult> CheckAsync(
        DatabaseOptions databaseOptions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Gates migrations that depend on the larger InnoDB index-key limit. The target database is
/// checked through a server connection first, so a fresh database cannot fail with an earlier
/// unknown-database error from migration history or legacy import.
/// </summary>
public sealed class InnoDbMigrationPreflight : IBusinessDatabaseMigrationPreflight
{
    public const long MinimumPageSizeBytes = 16 * 1024;

    public async Task<BusinessDatabaseMigrationPreflightResult> CheckAsync(
        DatabaseOptions databaseOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(databaseOptions);
        databaseOptions.Validate();

        if (databaseOptions.Provider is not (DatabaseProvider.MySql or DatabaseProvider.MariaDb))
        {
            return new BusinessDatabaseMigrationPreflightResult(true, [], []);
        }

        var contextOptions = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(contextOptions, databaseOptions);

        await using var context = new StructaDocDbContext(contextOptions.Options);
        var allMigrations = context.Database
            .GetService<IMigrationsAssembly>()
            .Migrations
            .Keys
            .ToArray();

        var qualifiedConnection = context.Database.GetDbConnection();
        var (databaseName, serverConnectionString) = CreateServerConnectionString(
            qualifiedConnection.ConnectionString);
        await using var serverConnection = CreateConnection(
            qualifiedConnection,
            serverConnectionString);
        await serverConnection.OpenAsync(cancellationToken);

        var databaseExists = await DatabaseExistsAsync(
            serverConnection,
            databaseName,
            cancellationToken);
        IReadOnlyList<string> pendingMigrations;
        if (databaseExists)
        {
            pendingMigrations = (await context.Database.GetPendingMigrationsAsync(cancellationToken))
                .ToArray();
        }
        else
        {
            // With no database (and therefore no history table), the complete migration assembly is
            // pending. Do not open the qualified connection to rediscover that fact.
            pendingMigrations = allMigrations;
        }

        var requirements = InnoDbIndexMigrationRegistry.FindPendingRequirements(pendingMigrations);
        if (requirements.Count == 0)
        {
            return new BusinessDatabaseMigrationPreflightResult(
                databaseExists,
                pendingMigrations,
                requirements);
        }

        var pageSize = await ReadInt64Async(
            serverConnection,
            "SELECT @@GLOBAL.innodb_page_size",
            cancellationToken);
        ValidatePageSize(pageSize);

        string? defaultRowFormat = null;
        foreach (var requirement in requirements
            .DistinctBy(requirement => requirement.TableName, StringComparer.Ordinal))
        {
            var actualRowFormat = databaseExists
                ? await ReadTableRowFormatAsync(
                    serverConnection,
                    databaseName,
                    requirement.TableName,
                    cancellationToken)
                : null;

            if (actualRowFormat is not null)
            {
                ValidateTableRowFormat(requirement, actualRowFormat);
                continue;
            }

            defaultRowFormat ??= await ReadStringAsync(
                serverConnection,
                "SELECT @@GLOBAL.innodb_default_row_format",
                cancellationToken);
            ValidateDefaultRowFormat(requirement, defaultRowFormat);
        }

        return new BusinessDatabaseMigrationPreflightResult(
            databaseExists,
            pendingMigrations,
            requirements);
    }

    public static void ValidatePageSize(long pageSizeBytes)
    {
        if (pageSizeBytes < MinimumPageSizeBytes)
        {
            throw new InvalidOperationException(
                $"The configured MySQL/MariaDB server uses innodb_page_size={pageSizeBytes} bytes. "
                + $"StructaDoc migrations that create large indexes require at least {MinimumPageSizeBytes} bytes. "
                + "Move the business database to an InnoDB server initialized with a supported page size before migrating.");
        }
    }

    public static void ValidateTableRowFormat(
        InnoDbIndexMigrationRequirement requirement,
        string rowFormat)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowFormat);

        if (!string.Equals(rowFormat, "DYNAMIC", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Table '{requirement.TableName}' uses InnoDB ROW_FORMAT={rowFormat}, but pending migration "
                + $"'{requirement.MigrationSuffix}' creates or rebuilds index '{requirement.IndexName}' and requires ROW_FORMAT=DYNAMIC. "
                + $"Convert the table with ALTER TABLE `{requirement.TableName}` ROW_FORMAT=DYNAMIC before migrating.");
        }
    }

    public static void ValidateDefaultRowFormat(
        InnoDbIndexMigrationRequirement requirement,
        string rowFormat)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowFormat);

        if (!string.Equals(rowFormat, "DYNAMIC", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The configured MySQL/MariaDB server uses innodb_default_row_format={rowFormat}, but pending migration "
                + $"'{requirement.MigrationSuffix}' will create table '{requirement.TableName}' or index '{requirement.IndexName}' and requires DYNAMIC rows. "
                + "Set innodb_default_row_format=DYNAMIC before migrating.");
        }
    }

    private static (string DatabaseName, string ServerConnectionString)
        CreateServerConnectionString(string connectionString)
    {
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString,
        };
        var databaseName = ReadAndRemove(builder, "Database")
            ?? ReadAndRemove(builder, "Initial Catalog");
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "Database:ConnectionString must select a business database for MySQL or MariaDB.");
        }

        // Remove both aliases even when the first one supplied the value. Driver-specific builders
        // reject duplicate aliases, but the generic builder can retain one from unusual input.
        builder.Remove("Database");
        builder.Remove("Initial Catalog");
        return (databaseName, builder.ConnectionString);
    }

    private static string? ReadAndRemove(DbConnectionStringBuilder builder, string key)
    {
        if (!builder.TryGetValue(key, out var value))
        {
            return null;
        }

        builder.Remove(key);
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static DbConnection CreateConnection(
        DbConnection template,
        string connectionString)
    {
        if (Activator.CreateInstance(template.GetType()) is not DbConnection connection)
        {
            throw new InvalidOperationException(
                $"Could not create a server connection for database driver '{template.GetType().FullName}'.");
        }

        connection.ConnectionString = connectionString;
        return connection;
    }

    private static async Task<bool> DatabaseExistsAsync(
        DbConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.SCHEMATA
            WHERE SCHEMA_NAME = @databaseName
            """;
        AddParameter(command, "@databaseName", databaseName);
        return await ReadInt64Async(command, cancellationToken) > 0;
    }

    private static async Task<string?> ReadTableRowFormatAsync(
        DbConnection connection,
        string databaseName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ROW_FORMAT
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @databaseName
              AND TABLE_NAME = @tableName
              AND TABLE_TYPE = 'BASE TABLE'
            """;
        AddParameter(command, "@databaseName", databaseName);
        AddParameter(command, "@tableName", tableName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadInt64Async(
        DbConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await ReadInt64Async(command, cancellationToken);
    }

    private static async Task<long> ReadInt64Async(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            throw new InvalidOperationException(
                $"The database server returned no value for preflight query '{command.CommandText}'.");
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadStringAsync(
        DbConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var text = value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException(
                $"The database server returned no value for preflight query '{commandText}'.")
            : text;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
