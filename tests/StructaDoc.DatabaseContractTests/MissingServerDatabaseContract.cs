using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using ServiceMantle.Migration;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.DatabaseContractTests;

internal static class MissingServerDatabaseContract
{
    public static async Task AssertAsync(DatabaseOptions existing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var missingName = $"structadoc_missing_{Guid.NewGuid():N}";
        var missing = Copy(existing, WithDatabase(existing, missingName));
        Assert.False(await ExistsAsync(existing, missingName, cancellationToken));

        // Real provider locks fail before either inspection or execution. A counting executor
        // additionally proves there is no EF CanConnect -> unleased executor fallback.
        await AssertGateRefusesAsync(missing, cancellationToken);
        Assert.Equal(1, await BusinessDatabaseMigrationCommandContract.ExecuteAsync(
            missing.Provider, missing.ConnectionString, missing.ServerVersion));
        Assert.False(await ExistsAsync(existing, missingName, cancellationToken));

        var invalidCredentials = Copy(existing, WithConnectionFailure(existing, wrongPassword: true));
        await AssertGateRefusesAsync(invalidCredentials, cancellationToken);
        var unreachable = Copy(existing, WithConnectionFailure(existing, wrongPassword: false));
        await AssertGateRefusesAsync(unreachable, cancellationToken);
        Assert.False(await ExistsAsync(existing, missingName, cancellationToken));
    }

    private static async Task AssertGateRefusesAsync(DatabaseOptions options, CancellationToken cancellationToken)
    {
        var executor = new ForbiddenExecutor();
        var registrations = new ServiceCollection();
        registrations.AddStructaDocPersistenceMigrationServices(options);
        registrations.AddScoped<IDatabaseMigrationExecutor>(_ => executor);
        await using var services = registrations.BuildServiceProvider();
        var result = await services.OrchestrateStructaDocMigrationsAsync(
            options, ServiceMantleMigrationOrchestration.ResolveDeploymentMode(options.Provider),
            cancellationToken: cancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, result.ErrorCode);
        Assert.False(result.ExecutorWasCalled);
        Assert.Equal(0, executor.Calls);
        Assert.DoesNotContain("missing-target-test-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(options.ConnectionString, result.ToString(), StringComparison.Ordinal);
    }

    private static DatabaseOptions Copy(DatabaseOptions source, string connectionString) => new()
    {
        Provider = source.Provider,
        ConnectionString = connectionString,
        ServerVersion = source.ServerVersion,
    };

    private static string WithDatabase(DatabaseOptions source, string database) =>
        source.Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = database }.ConnectionString
            : new MySqlConnectionStringBuilder(source.ConnectionString) { Database = database }.ConnectionString;

    private static string WithConnectionFailure(DatabaseOptions source, bool wrongPassword)
    {
        if (source.Provider == DatabaseProvider.PostgreSql)
        {
            var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString)
            {
                Pooling = false,
                Timeout = 1,
            };
            if (wrongPassword) builder.Password = "missing-target-test-secret";
            else builder.Port = 1;
            return builder.ConnectionString;
        }
        var mysql = new MySqlConnectionStringBuilder(source.ConnectionString)
        {
            Pooling = false,
            ConnectionTimeout = 1,
        };
        if (wrongPassword) mysql.Password = "missing-target-test-secret";
        else mysql.Port = 1;
        return mysql.ConnectionString;
    }

    private static async Task<bool> ExistsAsync(DatabaseOptions source, string database,
        CancellationToken cancellationToken)
    {
        if (source.Provider == DatabaseProvider.PostgreSql)
        {
            var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Database = "postgres" };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @database";
            command.Parameters.AddWithValue("database", database);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        var mysql = new MySqlConnectionStringBuilder(source.ConnectionString) { Database = string.Empty };
        await using var server = new MySqlConnection(mysql.ConnectionString);
        await server.OpenAsync(cancellationToken);
        await using var query = server.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @database";
        query.Parameters.AddWithValue("database", database);
        return Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private sealed class ForbiddenExecutor : IDatabaseMigrationExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A refused target must not be inspected.");
        }
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A refused target must not be migrated.");
        }
    }
}
