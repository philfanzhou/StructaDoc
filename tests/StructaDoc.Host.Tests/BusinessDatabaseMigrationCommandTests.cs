using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Host.Migrations;

namespace StructaDoc.Host.Tests;

public sealed class BusinessDatabaseMigrationCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"structadoc-migration-command-{Guid.NewGuid():N}");

    public BusinessDatabaseMigrationCommandTests()
    {
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void Operation_flag_is_removed_before_configuration_parsing()
    {
        var arguments = new[]
        {
            "--Database:Provider=Sqlite",
            BusinessDatabaseMigrationCommand.Flag,
            "--Storage:Provider=Local",
        };

        var requested = BusinessDatabaseMigrationCommand.TryExtractArguments(
            arguments,
            out var remaining);

        Assert.True(requested);
        Assert.Equal(
            ["--Database:Provider=Sqlite", "--Storage:Provider=Local"],
            remaining);
    }

    [Theory]
    [InlineData("--MIGRATE-BUSINESS-DATABASE")]
    [InlineData("--migrate-business-database=true")]
    public void Only_the_exact_value_less_operation_flag_is_recognised(string argument)
    {
        var arguments = new[] { argument };

        var requested = BusinessDatabaseMigrationCommand.TryExtractArguments(
            arguments,
            out var remaining);

        Assert.False(requested);
        Assert.Same(arguments, remaining);
    }

    [Fact]
    public async Task Published_entry_point_migrates_once_without_starting_the_application()
    {
        var controlPlanePath = Path.Combine(directory, "control.db");
        var businessPath = Path.Combine(directory, "business.db");

        var first = await RunAsync(
            $"--ControlPlane:DatabasePath={controlPlanePath}",
            $"--Database:ConnectionString=Data Source={businessPath};Pooling=False",
            "--Database:ApplyMigrationsOnStartup=false",
            "--Storage:Provider=not-a-storage-provider",
            "--Oidc:Enabled=true",
            "--Worker:LeaseDuration=not-a-duration");

        Assert.Equal(0, first.ExitCode);
        Assert.DoesNotContain("Now listening", first.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(await HasMigrationsAsync(controlPlanePath));
        Assert.True(await HasMigrationsAsync(businessPath));
        Assert.Equal(0, await CountRowsAsync(controlPlanePath, "admin_users"));
        Assert.Equal(0, await CountRowsAsync(businessPath, "provider_configs"));

        // A normal start would layer these browser-stored values over the unpinned Provider and
        // ServerVersion keys and fail validation. The one-shot path reads deployment configuration
        // only, so the default SQLite Provider remains in force.
        await AddStoredSettingAsync(controlPlanePath, "Database:Provider", "MySql");
        await AddStoredSettingAsync(controlPlanePath, "Database:ServerVersion", "not-a-version");
        File.Delete(businessPath);

        var second = await RunAsync(
            $"--ControlPlane:DatabasePath={controlPlanePath}",
            $"--Database:ConnectionString=Data Source={businessPath};Pooling=False",
            "--Database:ApplyMigrationsOnStartup=false");
        var third = await RunAsync(
            $"--ControlPlane:DatabasePath={controlPlanePath}",
            $"--Database:ConnectionString=Data Source={businessPath};Pooling=False",
            "--Database:ApplyMigrationsOnStartup=false");

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(0, third.ExitCode);
        Assert.DoesNotContain("Now listening", second.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Now listening", third.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(await HasMigrationsAsync(businessPath));
    }

    [Fact]
    public async Task Business_configuration_failure_is_nonzero_sanitized_and_follows_control_plane_migration()
    {
        var controlPlanePath = Path.Combine(directory, "failure-control.db");
        const string secret = "migration-command-secret";

        var result = await RunAsync(
            $"--ControlPlane:DatabasePath={controlPlanePath}",
            "--Database:Provider=MySql",
            $"--Database:ConnectionString=Server=localhost;Database=structadoc;User ID=user;Password={secret}",
            "--Database:ServerVersion=not-a-version",
            "--Database:ApplyMigrationsOnStartup=false");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("database migration failed", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Database:ServerVersion", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Output, StringComparison.Ordinal);
        Assert.True(await HasMigrationsAsync(controlPlanePath));
        Assert.DoesNotContain("Now listening", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_database_imports_legacy_administrators_before_business_migration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var controlPlanePath = Path.Combine(directory, "legacy-control.db");
        var businessPath = Path.Combine(directory, "legacy-business.db");
        var administratorId = Guid.NewGuid();
        var securityStamp = Guid.NewGuid();
        var options = new DatabaseOptions
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = $"Data Source={businessPath};Pooling=False",
            ApplyMigrationsOnStartup = false,
        };
        var contextOptions = new DbContextOptionsBuilder<StructaDocDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDatabase(contextOptions, options);
        await using (var context = new StructaDocDbContext(contextOptions.Options))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(
                "20260807101514_AddUserWorkspaceLifecycleAndSegments",
                cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO admin_users
                    (id, display_name, email, normalized_email, password_hash, is_active,
                     security_stamp, created_at_utc, last_login_at_utc)
                VALUES
                    ({administratorId}, {"Legacy Administrator"}, {"legacy@example.test"},
                     {"LEGACY@EXAMPLE.TEST"}, {"test-password-hash"}, {true},
                     {securityStamp}, {DateTime.UnixEpoch}, {null})
                """, cancellationToken);
        }

        var result = await RunAsync(
            $"--ControlPlane:DatabasePath={controlPlanePath}",
            $"--Database:ConnectionString=Data Source={businessPath};Pooling=False",
            "--Database:ApplyMigrationsOnStartup=false");

        Assert.Equal(0, result.ExitCode);
        await using var control = new SqliteConnection(
            $"Data Source={controlPlanePath};Mode=ReadOnly;Pooling=False");
        await control.OpenAsync(cancellationToken);
        await using (var command = control.CreateCommand())
        {
            command.CommandText = "SELECT legacy_normalized_login FROM admin_users";
            Assert.Equal(
                "LEGACY@EXAMPLE.TEST",
                await command.ExecuteScalarAsync(cancellationToken));
        }

        await using var business = new SqliteConnection(
            $"Data Source={businessPath};Mode=ReadOnly;Pooling=False");
        await business.OpenAsync(cancellationToken);
        await using (var command = business.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'admin_users'";
            Assert.Equal(
                0,
                Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)));
        }
    }

    [Fact]
    public async Task Business_migration_failure_returns_nonzero_after_control_plane_succeeds()
    {
        var controlPlanePath = Path.Combine(directory, "migration-failure-control.db");
        var directoryUsedAsDatabase = Path.Combine(directory, "not-a-database-file");
        Directory.CreateDirectory(directoryUsedAsDatabase);

        var result = await RunAsync(
            $"--ControlPlane:DatabasePath={controlPlanePath}",
            $"--Database:ConnectionString=Data Source={directoryUsedAsDatabase};Pooling=False",
            "--Database:ApplyMigrationsOnStartup=false");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("database migration failed", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(await HasMigrationsAsync(controlPlanePath));
        Assert.DoesNotContain("Now listening", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CommandResult> RunAsync(params string[] deploymentArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(BusinessDatabaseMigrationCommand.Flag);
        foreach (var argument in deploymentArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The migration command process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return new CommandResult(
            process.ExitCode,
            (await standardOutput) + Environment.NewLine + (await standardError));
    }

    private static async Task<bool> HasMigrationsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<int> CountRowsAsync(string databasePath, string tableName)
    {
        Assert.Matches("^[a-z_]+$", tableName);
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AddStoredSettingAsync(
        string controlPlanePath,
        string key,
        string value)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={controlPlanePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value, updated_at_utc, updated_by)
            VALUES ($key, $value, $updatedAtUtc, $updatedBy)
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow);
        command.Parameters.AddWithValue("$updatedBy", "test");
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
