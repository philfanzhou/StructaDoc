using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Settings;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Health;

namespace StructaDoc.Host.Tests;

// The live and readiness endpoints are the ServiceMantle health endpoints reading one
// StructaDocHealthSnapshotSource per request. Live never resolves application state. Readiness is
// 200 only for the Completed + Succeeded + Reachable snapshot, which holds the contract the
// replaced health checks published: the control-plane database answers, the business database
// carries no startup fault, and the business database still answers. The body is JSON with a fixed
// field set and a stable error code per failure form; only the status code is a deployment's
// contract, which is what the Docker HEALTHCHECK and CI use.
public sealed class HealthEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    private const string TooNewMigrationId = "20990101000000_FromTheFuture";

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Healthy_deployment_answers_ok(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_body_reports_the_fixed_field_set()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = await ReadBodyAsync(response);
        var root = body.RootElement;
        Assert.Equal(
            ["databaseStatus", "errorCode", "migrationStatus", "phase", "status"],
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal("completed", root.GetProperty("phase").GetString());
        Assert.Equal("succeeded", root.GetProperty("migrationStatus").GetString());
        Assert.Equal("reachable", root.GetProperty("databaseStatus").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("errorCode").ValueKind);
    }

    [Fact]
    public async Task Live_body_is_the_status_alone()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        var root = body.RootElement;
        Assert.Equal(["status"], root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("live", root.GetProperty("status").GetString());
    }

    // The new /health alias is a readiness alias: the same decision, the same body shape.
    [Fact]
    public async Task The_health_alias_answers_what_ready_answers()
    {
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var alias = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(ready.StatusCode, alias.StatusCode);
        Assert.Equal(
            await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await alias.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // The four failure forms that must answer 503 exactly as the replaced checks did: a stored
    // business-database fault, a control plane lost after startup, and a business database lost
    // after startup (a server database whose endpoint refuses connections). Each carries its own
    // stable error code, and none of the bodies contains a connection string or exception text.
    [Fact]
    public async Task A_stored_business_database_fault_fails_readiness_and_leaves_live_ok()
    {
        using var deployment = new SettingsTestDeployment();
        var futureDatabasePath = Path.Combine(
            Path.GetDirectoryName(deployment.ControlPlanePath)!,
            "future.db");
        await SeedTooNewHistoryAsync(futureDatabasePath);

        using (var writer = UnpinnedFactory(deployment))
        using (var writerClient = await SettingsTestDeployment.SignedInClientAsync(writer))
        {
            using var write = await writerClient.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(
                    SettingCatalog.DatabaseConnectionString,
                    $"Data Source={futureDatabasePath};Pooling=False"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        using var restarted = deployment.CreateFactory(pinBusinessDatabase: false);
        using var client = restarted.CreateClient();

        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var body = await ReadBodyAsync(ready);
        var root = body.RootElement;
        Assert.Equal("not_ready", root.GetProperty("status").GetString());
        Assert.Equal("completed", root.GetProperty("phase").GetString());
        Assert.Equal("failed", root.GetProperty("migrationStatus").GetString());
        Assert.Equal(
            StructaDocHealthSnapshotSource.DatabaseStartupFaultErrorCode,
            root.GetProperty("errorCode").GetString());

        // Live never resolves application state, so the same fault leaves it answering 200 —
        // which is what keeps a HEALTHCHECK on /health/live from restarting a fixable container.
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task A_control_plane_lost_after_startup_fails_readiness_with_a_stable_error_code()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = factory.CreateClient();

        using var healthy = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        // The control-plane database disappears after startup: pooling keeps its file handle, so
        // the pools are cleared first, and a directory cannot be opened as a SQLite database.
        SqliteConnection.ClearAllPools();
        File.Delete(deployment.ControlPlanePath);
        Directory.CreateDirectory(deployment.ControlPlanePath);

        using var notReady = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        using var body = await ReadBodyAsync(notReady);
        var root = body.RootElement;
        Assert.Equal("not_ready", root.GetProperty("status").GetString());
        Assert.Equal("unreachable", root.GetProperty("databaseStatus").GetString());
        Assert.Equal(
            StructaDocHealthSnapshotSource.ControlPlaneUnreachableErrorCode,
            root.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_business_database_lost_after_startup_fails_readiness_with_a_stable_error_code()
    {
        using var deployment = new SettingsTestDeployment();
        // A business database the service can no longer open, configured without startup
        // migration so the host starts without ever reaching it — the "lost after startup" form.
        // A server database that refuses connections would also answer 503, but its EF execution
        // strategy retries past the probe budget, so the simulated loss uses an unopenable SQLite
        // target: the probe fails fast and the snapshot states the database is unreachable.
        var unopenableTarget = Path.GetDirectoryName(deployment.ControlPlanePath)!;
        using var factory = deployment.CreateFactory(builder =>
        {
            builder.UseSetting(
                "Database:ConnectionString",
                $"Data Source={unopenableTarget};Pooling=False");
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        });
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var body = await ReadBodyAsync(ready);
        var root = body.RootElement;
        Assert.Equal("not_ready", root.GetProperty("status").GetString());
        Assert.Equal("completed", root.GetProperty("phase").GetString());
        Assert.Equal("succeeded", root.GetProperty("migrationStatus").GetString());
        Assert.Equal("unreachable", root.GetProperty("databaseStatus").GetString());
        Assert.Equal(
            StructaDocHealthSnapshotSource.DatabaseUnreachableErrorCode,
            root.GetProperty("errorCode").GetString());
    }

    // A source that fails is an internal failure, not a state: the endpoint fails closed with the
    // library's probe-failed code and null state fields.
    [Fact]
    public async Task A_failing_snapshot_source_fails_closed()
    {
        using var probeFactory = factory.WithWebHostBuilder(
            builder => builder.ConfigureServices(services => services.AddScoped<IServiceHealthSnapshotSource>(
                _ => new ThrowingSnapshotSource())));
        using var client = probeFactory.CreateClient();

        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = await ReadBodyAsync(response);
        var root = body.RootElement;
        Assert.Equal("not_ready", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("phase").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("migrationStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("databaseStatus").ValueKind);
        Assert.Equal("health.probe_failed", root.GetProperty("errorCode").GetString());
    }

    // Caller cancellation is distinct from a probe failure: the request ends cancelled rather than
    // answered with a 503.
    [Fact]
    public async Task Caller_request_cancellation_propagates()
    {
        using var probeFactory = factory.WithWebHostBuilder(
            builder => builder.ConfigureServices(services => services.AddScoped<IServiceHealthSnapshotSource>(
                _ => new NeverAnsweringSnapshotSource())));
        using var client = probeFactory.CreateClient();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/health/ready", cancellation.Token));
    }

    private static async Task<JsonDocument> ReadBodyAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task SeedTooNewHistoryAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
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

    private static WebApplicationFactory<Program> UnpinnedFactory(SettingsTestDeployment deployment)
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

    private sealed class ThrowingSnapshotSource : IServiceHealthSnapshotSource
    {
        public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The probe failed for test purposes.");
    }

    private sealed class NeverAnsweringSnapshotSource : IServiceHealthSnapshotSource
    {
        public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new ServiceHealthSnapshot(
                ServiceStartupPhase.Completed,
                ServiceMigrationReadinessState.Succeeded,
                ServiceDatabaseReadinessState.Reachable,
                errorCode: null);
        }
    }
}
