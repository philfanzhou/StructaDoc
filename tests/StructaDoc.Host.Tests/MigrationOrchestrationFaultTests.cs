using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Settings;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The migration orchestration failure boundary from issue #103: a database whose history is newer
/// than this build fails closed with a stable error code, and the existing startup contract decides
/// what that means — a stored configuration records a fault and keeps the administration area
/// available, while a deployment-pinned configuration stops startup.
/// </summary>
public sealed class MigrationOrchestrationFaultTests
{
    private const string TooNewMigrationId = "20990101000000_FromTheFuture";

    [Fact]
    public async Task A_stored_orchestration_failure_is_recoverable_and_fails_readiness()
    {
        using var deployment = new SettingsTestDeployment();
        var futureDatabasePath = Path.Combine(
            Path.GetDirectoryName(deployment.ControlPlanePath)!,
            "future.db");
        await SeedTooNewHistoryAsync(futureDatabasePath);

        using (var writer = UnpinnedFactory(deployment))
        using (var client = await SettingsTestDeployment.SignedInClientAsync(writer))
        {
            using var write = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(
                    SettingCatalog.DatabaseConnectionString,
                    $"Data Source={futureDatabasePath};Pooling=False"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        // The next start reads what was saved. The orchestration fails closed on the newer
        // history, and refusing to start would take away the only surface the mistake can be
        // corrected from.
        using var restarted = deployment.CreateFactory(pinBusinessDatabase: false);
        using var administrator = await SettingsTestDeployment.SignedInClientAsync(restarted);

        var database = await administrator.GetFromJsonAsync<DatabaseStatusResponse>(
            "/api/v1/admin/settings/database",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(database!.StartupFault);
        Assert.Contains(
            WellKnownMigrationErrorCodeTokens.VersionTooNew,
            database.StartupFault,
            StringComparison.Ordinal);

        // Signing in and reading settings both work, because administrators and settings live in
        // the control plane rather than in the database that was rejected.
        var settings = await administrator.GetFromJsonAsync<SettingResponse[]>(
            "/api/v1/admin/settings",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(settings!);

        // Nothing routes real traffic to it, though.
        using var ready = await administrator.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task A_pinned_orchestration_failure_stops_startup()
    {
        using var deployment = new SettingsTestDeployment();
        var futureDatabasePath = Path.Combine(
            Path.GetDirectoryName(deployment.ControlPlanePath)!,
            "pinned-future.db");
        await SeedTooNewHistoryAsync(futureDatabasePath);

        using var factory = deployment.CreateFactory(builder => builder.UseSetting(
            "Database:ConnectionString",
            $"Data Source={futureDatabasePath};Pooling=False"));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(
            WellKnownMigrationErrorCodeTokens.VersionTooNew,
            error.ToString(),
            StringComparison.Ordinal);
    }

    private static async Task SeedTooNewHistoryAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES (@migrationId, '10.0.11')
            """;
        command.Parameters.AddWithValue("@migrationId", TooNewMigrationId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> UnpinnedFactory(
        SettingsTestDeployment deployment)
    {
        return deployment.CreateFactory(builder => builder.ConfigureServices(
            (context, services) =>
            {
                services.AddSingleton(StructaDocSettingsConfiguration.Create(
                    context.Configuration,
                    new ControlPlaneOptions
                    {
                        DatabasePath = context.Configuration["ControlPlane:DatabasePath"]!,
                    },
                    [],
                    new FakeSettingSecretProtector(),
                    new SettingsStartupFault()));
            }));
    }

    /// <summary>
    /// The stable error codes the ServiceMantle orchestration reports. Duplicating the constants
    /// keeps the test independent of the library's exact text while still pinning the token the
    /// operator sees in a fault or a startup failure.
    /// </summary>
    private static class WellKnownMigrationErrorCodeTokens
    {
        public const string VersionTooNew = "migration.version_too_new";
    }
}
