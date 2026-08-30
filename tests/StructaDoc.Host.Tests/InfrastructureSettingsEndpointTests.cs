using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Storage;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Where documents are kept and where business data lives, managed from the browser.
///
/// These are the two settings a deployment could previously only change by recreating its container,
/// which the product's operator is not expected to be able to do. They are also the two whose wrong
/// value leaves nothing else working, so the tests here are as much about what survives being wrong
/// as about the writes succeeding.
/// </summary>
public sealed class InfrastructureSettingsEndpointTests
{
    [Fact]
    public async Task Storage_and_database_report_what_the_running_service_uses()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        var storage = await client.GetFromJsonAsync<StorageStatusResponse>(
            "/api/v1/admin/settings/storage",
            cancellationToken: TestContext.Current.CancellationToken);
        var database = await client.GetFromJsonAsync<DatabaseStatusResponse>(
            "/api/v1/admin/settings/database",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Local", storage!.Provider);
        Assert.Null(storage.StartupFault);
        Assert.False(storage.HasCredential);

        // Reported by asking the database rather than by remembering how startup went, because the
        // interesting case is one that went away after the service came up.
        Assert.Equal("Sqlite", database!.Provider);
        Assert.True(database.IsReachable);
        Assert.False(database.HasPendingMigrations);
        Assert.Null(database.StartupFault);
    }

    [Fact]
    public async Task A_storage_location_is_tested_before_it_is_committed_to()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);
        var candidatePath = Path.Combine(Path.GetTempPath(), $"structadoc-probe-{Guid.NewGuid():N}");

        try
        {
            // Writing, not listing. A location that can be read but not written to accepts every
            // upload attempt and fails each one.
            var writable = await TestStorageAsync(
                client,
                new StorageConnectionTestRequest(Provider: "Local", RootPath: candidatePath));
            Assert.True(writable.Succeeded);
            Assert.Equal(nameof(StorageProbeCode.Writable), writable.Code);

            // S3 without a bucket is not a location at all, and saying so before a restart is the
            // whole point of a test button.
            var incomplete = await TestStorageAsync(
                client,
                new StorageConnectionTestRequest(Provider: "S3", Bucket: null));
            Assert.False(incomplete.Succeeded);
            Assert.Equal(nameof(StorageProbeCode.InvalidConfiguration), incomplete.Code);
        }
        finally
        {
            if (Directory.Exists(candidatePath))
            {
                Directory.Delete(candidatePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_database_is_tested_before_it_is_committed_to()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        // Omitted fields fall back to what is in force, so the connection string the service never
        // sends back does not have to be retyped to test what is already saved.
        var current = await TestDatabaseAsync(client, new DatabaseConnectionTestRequest());
        Assert.True(current.Succeeded);
        Assert.Equal(nameof(DatabaseProbeCode.Reachable), current.Code);

        var missing = await TestDatabaseAsync(
            client,
            new DatabaseConnectionTestRequest(
                Provider: "Sqlite",
                ConnectionString: $"Data Source={Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.db")}"));
        Assert.False(missing.Succeeded);
        Assert.Equal(nameof(DatabaseProbeCode.Unreachable), missing.Code);

        var unsupported = await TestDatabaseAsync(
            client,
            new DatabaseConnectionTestRequest(Provider: "Oracle"));
        Assert.False(unsupported.Succeeded);
        Assert.Equal(nameof(DatabaseProbeCode.InvalidConfiguration), unsupported.Code);

        // MySQL and MariaDB need a version the Host will not infer by connecting, so a configuration
        // without one is refused rather than guessed at.
        var versionless = await TestDatabaseAsync(
            client,
            new DatabaseConnectionTestRequest(
                Provider: "MySql",
                ConnectionString: "Server=localhost;Database=structadoc;User Id=structadoc;Password=x"));
        Assert.False(versionless.Succeeded);
        Assert.Equal(nameof(DatabaseProbeCode.InvalidConfiguration), versionless.Code);
    }

    [Fact]
    public async Task Storage_and_database_values_are_stored_and_reported_as_pending_a_restart()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = UnpinnedFactory(deployment);
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        using var write = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.StorageProvider, "s3"),
            cancellationToken: TestContext.Current.CancellationToken);
        var result = await write.Content.ReadFromJsonAsync<SettingUpdateResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        // Nothing re-reads storage while the service runs, so claiming an effect would be a lie.
        Assert.True(result!.RestartRequired);

        var stored = await GetAsync(client, SettingCatalog.StorageProvider);
        Assert.True(stored.IsStored);
        Assert.True(stored.IsPendingRestart);
        // A closed set answers in its own spelling, so what is stored is what the options class
        // parses rather than whatever casing was typed.
        Assert.Equal("S3", stored.Value);
        Assert.Equal(["Local", "S3"], stored.AllowedValues);

        using var secret = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(SettingCatalog.DatabaseConnectionString, "Data Source=/data/moved.db"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, secret.StatusCode);

        var connectionString = await GetAsync(client, SettingCatalog.DatabaseConnectionString);
        Assert.True(connectionString.IsStored);
        // A connection string usually carries a password, so only whether one is set comes back.
        Assert.Equal(string.Empty, connectionString.Value);

        var listed = await client.GetStringAsync(
            "/api/v1/admin/settings",
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("moved.db", listed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SettingCatalog.DatabaseProvider, "Oracle")]
    [InlineData(SettingCatalog.StorageProvider, "Ftp")]
    public async Task A_value_outside_the_closed_set_is_refused_while_it_is_still_on_screen(
        string key,
        string value)
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = UnpinnedFactory(deployment);
        using var client = await SettingsTestDeployment.SignedInClientAsync(factory);

        // Refused at the moment it is written rather than at the next restart, which is the only
        // time an administrator is still looking at what they typed.
        using var response = await client.PutAsJsonAsync(
            "/api/v1/admin/settings",
            new SettingUpdateRequest(key, value),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await GetAsync(client, key)).IsStored);
    }

    [Fact]
    public async Task A_business_database_that_cannot_be_prepared_leaves_the_administration_area_working()
    {
        using var deployment = new SettingsTestDeployment();

        // An existing file cannot also be a directory, so this is a SQLite location the Host cannot
        // create on any operating system: the same shape as a server that is not there, without
        // waiting for a connection to time out.
        var blocker = Path.Combine(Path.GetTempPath(), $"structadoc-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(
            blocker,
            "not a directory",
            TestContext.Current.CancellationToken);

        try
        {
            using (var writer = UnpinnedFactory(deployment))
            using (var client = await SettingsTestDeployment.SignedInClientAsync(writer))
            {
                using var write = await client.PutAsJsonAsync(
                    "/api/v1/admin/settings",
                    new SettingUpdateRequest(
                        SettingCatalog.DatabaseConnectionString,
                        $"Data Source={Path.Combine(blocker, "structadoc.db")}"),
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            }

            // The next start reads what was saved. Refusing to start on it would take away the only
            // surface the mistake can be corrected from.
            using var restarted = deployment.CreateFactory(pinBusinessDatabase: false);
            using var administrator = await SettingsTestDeployment.SignedInClientAsync(restarted);

            var database = await administrator.GetFromJsonAsync<DatabaseStatusResponse>(
                "/api/v1/admin/settings/database",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(database!.StartupFault);
            Assert.False(database.IsReachable);

            // Signing in and reading settings both work, because administrators and settings live in
            // the control plane rather than in the database that is missing.
            var settings = await administrator.GetFromJsonAsync<SettingResponse[]>(
                "/api/v1/admin/settings",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEmpty(settings!);

            // Nothing routes real traffic to it, though. A service that answers /admin and cannot
            // store a document is not ready.
            using var ready = await administrator.GetAsync(
                "/health/ready",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

            // Clearing the stored value is the repair, and it is reachable from here.
            using var repair = await administrator.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(SettingCatalog.DatabaseConnectionString, string.Empty),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, repair.StatusCode);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task A_stored_database_preflight_failure_is_recoverable_and_fails_readiness()
    {
        using var deployment = new SettingsTestDeployment();
        using (var writer = UnpinnedFactory(deployment))
        using (var client = await SettingsTestDeployment.SignedInClientAsync(writer))
        {
            using var write = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(
                    SettingCatalog.DatabaseConnectionString,
                    $"Data Source={deployment.BusinessDatabasePath};Pooling=False"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        using var restarted = deployment.CreateFactory(
            builder => builder.ConfigureServices(
                services =>
                {
                    services.RemoveAll<IBusinessDatabaseMigrationPreflight>();
                    services.AddSingleton<IBusinessDatabaseMigrationPreflight>(
                        new RejectingMigrationPreflight());
                }),
            pinBusinessDatabase: false);
        using var administrator = await SettingsTestDeployment.SignedInClientAsync(restarted);

        var database = await administrator.GetFromJsonAsync<DatabaseStatusResponse>(
            "/api/v1/admin/settings/database",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(
            RejectingMigrationPreflight.Detail,
            database!.StartupFault,
            StringComparison.Ordinal);

        using var ready = await administrator.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public void A_deployment_database_preflight_failure_stops_startup()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory(builder => builder.ConfigureServices(
            services =>
            {
                services.RemoveAll<IBusinessDatabaseMigrationPreflight>();
                services.AddSingleton<IBusinessDatabaseMigrationPreflight>(
                    new RejectingMigrationPreflight());
            }));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(
            RejectingMigrationPreflight.Detail,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Infrastructure_status_and_tests_are_closed_to_anonymous_callers()
    {
        using var deployment = new SettingsTestDeployment();
        using var factory = deployment.CreateFactory();
        using var client = factory.CreateClient();

        using var storage = await client.GetAsync(
            "/api/v1/admin/settings/storage",
            TestContext.Current.CancellationToken);
        using var database = await client.GetAsync(
            "/api/v1/admin/settings/database",
            TestContext.Current.CancellationToken);
        using var test = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/database/test",
            new DatabaseConnectionTestRequest(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, storage.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, database.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, test.StatusCode);
    }

    /// <summary>
    /// The test host passes every <c>UseSetting</c> value to the entry point as a command-line
    /// argument, which is a pin, so a Host configured that way reports storage and the database as
    /// managed by the deployment and refuses to write them. Replacing the registration with one that
    /// was given no arguments is how a test reaches the case a real deployment is in.
    /// </summary>
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

    private static async Task<ConnectionTestResponse> TestStorageAsync(
        HttpClient client,
        StorageConnectionTestRequest request)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/storage/test",
            request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConnectionTestResponse>())!;
    }

    private static async Task<ConnectionTestResponse> TestDatabaseAsync(
        HttpClient client,
        DatabaseConnectionTestRequest request)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/settings/database/test",
            request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConnectionTestResponse>())!;
    }

    private static async Task<SettingResponse> GetAsync(HttpClient client, string key)
    {
        var settings = await client.GetFromJsonAsync<SettingResponse[]>("/api/v1/admin/settings");
        return settings!.Single(setting => setting.Key == key);
    }

    private sealed class RejectingMigrationPreflight : IBusinessDatabaseMigrationPreflight
    {
        public const string Detail = "The test migration preflight rejected this database.";

        public Task<BusinessDatabaseMigrationPreflightResult> CheckAsync(
            DatabaseOptions databaseOptions,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(Detail);
        }
    }
}
