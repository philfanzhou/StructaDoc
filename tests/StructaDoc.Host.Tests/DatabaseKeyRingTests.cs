using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Persistence.Relational.DataProtection;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The Data Protection key ring's database form from #114: keys live in the business database
/// under an authenticated envelope keyed by the deployment's root key, so instances pointing at
/// the same database with the same root key decrypt each other's material, a wrong root key fails
/// closed, and the form is only available when the business database is pinned rather than stored.
/// </summary>
public sealed class DatabaseKeyRingTests
{
    private const string RootKey = "test-root-key-0001";

    [Fact]
    public async Task A_second_instance_decrypts_what_the_first_encrypted()
    {
        using var deployment = new KeyRingDeployment();
        await deployment.MigrateBusinessDatabaseAsync();

        using var first = await deployment.StartHostAsync("instance-a-control.db", RootKey);
        var credential = await CreateApiClientCredentialAsync(first);

        // The ring row exists in the business database, which is the shared state this form is for.
        Assert.True(deployment.HasKeyRingRows());

        // A second host — its own control plane and key directory, the same business database and
        // root key — authenticates the credential the first host protected. This is the
        // multi-instance guarantee: cookies and credentials survive being answered elsewhere.
        using var second = await deployment.StartHostAsync("instance-b-control.db", RootKey);
        using var authenticated = second.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            credential);

        using var response = await authenticated.GetAsync(
            "/api/v1/documents",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The file form is untouched by this wiring: the default host still writes its key ring to the
    // keys directory and nothing to the business database.
    [Fact]
    public async Task The_file_form_still_writes_keys_to_the_directory()
    {
        using var deployment = new KeyRingDeployment();

        using var host = await deployment.StartFileHostAsync("file-control.db");
        using var client = host.CreateClient();
        await client.LoginAsAdministratorAsync();

        Assert.True(Directory.GetFiles(deployment.KeysPath).Length > 0);
        Assert.False(deployment.HasKeyRingRows());
    }

    [Fact]
    public void A_wrong_root_key_fails_closed_without_leaking_key_material()
    {
        using var deployment = new KeyRingDeployment();
        deployment.MigrateBusinessDatabaseBlocking();

        var rightRing = StructaDocKeyRing.CreateDatabase(
            new StructaDocAuthenticationOptions
            {
                DataProtectionKeyPersistence = DataProtectionKeyPersistence.Database,
                DataProtectionRootKey = RootKey,
            },
            deployment.DatabaseOptions);
        var protectedValue = rightRing.CreateProtector("probe").Protect("shared secret");

        var wrongRing = StructaDocKeyRing.CreateDatabase(
            new StructaDocAuthenticationOptions
            {
                DataProtectionKeyPersistence = DataProtectionKeyPersistence.Database,
                DataProtectionRootKey = "a-different-root-key",
            },
            deployment.DatabaseOptions);

        // Reading the ring with the wrong root key fails closed: the stored envelope cannot be
        // opened, and the error names neither root key.
        var error = Assert.ThrowsAny<Exception>(
            () => wrongRing.CreateProtector("probe").Unprotect(protectedValue));
        var text = error.ToString();
        Assert.Contains("data_protection_keys", text, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("a-different-root-key", text, StringComparison.Ordinal);
    }
    [Fact]
    public async Task A_database_key_ring_requires_its_root_key_at_startup()
    {
        using var deployment = new KeyRingDeployment();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => deployment.StartHostAsync("no-root-key-control.db", rootKey: null));
        Assert.Contains(
            "Authentication:DataProtectionRootKey must be configured",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_business_database_section_refuses_the_database_form()
    {
        using var deployment = new KeyRingDeployment();
        // A stored Database section — written through the browser, as the settings area allows —
        // is the one configuration the database key ring cannot run on: the connection string
        // would be material the ring itself has to decrypt.
        using (var writer = deployment.CreateUnpinnedWriterHost())
        using (var client = await SettingsTestDeployment.SignedInClientAsync(writer))
        {
            using var write = await client.PutAsJsonAsync(
                "/api/v1/admin/settings",
                new SettingUpdateRequest(
                    SettingCatalog.DatabaseConnectionString,
                    "Data Source=stored.db;Pooling=False"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => deployment.StartHostAsync(
                "shared-control.db",
                RootKey,
                pinBusinessDatabase: false));
        Assert.Contains(
            "Authentication:DataProtectionKeyPersistence",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Pin the business database (Database__*)",
            error.Message,
            StringComparison.Ordinal);
    }

    private static async Task<string> CreateApiClientCredentialAsync(
        WebApplicationFactory<Program> factory)
    {
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();
        using var response = await administrator.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest("Cross-instance probe", [AuthenticationScopes.DocumentsRead]),
            cancellationToken: TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<ApiClientCredentialResponse>(
                TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("API client creation returned no response.");
        return created.Credential;
    }

    /// <summary>
    /// One directory holding the two things the database form shares between instances — the
    /// business database and the root key — and per-instance control planes and key directories
    /// that are deliberately not shared.
    /// </summary>
    private sealed class KeyRingDeployment : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "structadoc-key-ring-tests",
            Guid.NewGuid().ToString("N"));

        public KeyRingDeployment()
        {
            Directory.CreateDirectory(directory);
        }

        public string BusinessDatabasePath => Path.Combine(directory, "structadoc.db");

        public string KeysPath => Path.Combine(directory, "keys");

        public DatabaseOptions DatabaseOptions => new()
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = $"Data Source={BusinessDatabasePath};Pooling=False",
            ApplyMigrationsOnStartup = true,
        };

        public async Task MigrateBusinessDatabaseAsync()
        {
            var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();
            PersistenceServiceCollectionExtensions.ConfigureDatabase(optionsBuilder, DatabaseOptions);
            await using var context = new StructaDocDbContext(optionsBuilder.Options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        public void MigrateBusinessDatabaseBlocking()
        {
            MigrateBusinessDatabaseAsync().GetAwaiter().GetResult();
        }

        public async Task<WebApplicationFactory<Program>> StartHostAsync(
            string controlPlaneFileName,
            string? rootKey,
            bool pinBusinessDatabase = true)
        {
            var factory = new KeyRingFactory(
                directory,
                Path.Combine(directory, controlPlaneFileName),
                databaseKeyRing: true,
                rootKey,
                pinBusinessDatabase,
                StructaDocWebApplicationFactory.AdministratorUsername,
                StructaDocWebApplicationFactory.AdministratorPassword);
            // Creating the client runs the entry point, which is where the startup failures under
            // test surface; signing in then drives the ring through a real request.
            using var client = factory.CreateClient();
            await client.LoginAsAdministratorAsync();
            return factory;
        }

        public async Task<WebApplicationFactory<Program>> StartFileHostAsync(
            string controlPlaneFileName)
        {
            var factory = new KeyRingFactory(
                directory,
                Path.Combine(directory, controlPlaneFileName),
                databaseKeyRing: false,
                rootKey: null,
                pinBusinessDatabase: true,
                StructaDocWebApplicationFactory.AdministratorUsername,
                StructaDocWebApplicationFactory.AdministratorPassword);
            using var client = factory.CreateClient();
            await client.LoginAsAdministratorAsync();
            return factory;
        }

        /// <summary>
        /// A host whose settings configuration sees no command-line pins and protects nothing, the
        /// established pattern for writing stored settings from tests. Its business database stays
        /// pinned — the container default is what must not be reached — but the write path does not
        /// see that pin, so the Database connection string is storable through the browser.
        /// </summary>
        public WebApplicationFactory<Program> CreateUnpinnedWriterHost()
        {
            return new KeyRingFactory(
                directory,
                Path.Combine(directory, "shared-control.db"),
                databaseKeyRing: false,
                rootKey: null,
                pinBusinessDatabase: true,
                SettingsTestDeployment.Username,
                SettingsTestDeployment.Password,
                fakeProtector: true);
        }

        public bool HasKeyRingRows()
        {
            if (!File.Exists(BusinessDatabasePath))
            {
                return false;
            }

            using var connection = new SqliteConnection($"Data Source={BusinessDatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM service_data_protection_keys";
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }

        private sealed class KeyRingFactory(
            string directory,
            string controlPlanePath,
            bool databaseKeyRing,
            string? rootKey,
            bool pinBusinessDatabase,
            string administratorUsername,
            string administratorPassword,
            bool fakeProtector = false)
            : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Worker:Enabled", "false");
                builder.UseSetting(
                    "Authentication:BootstrapAdministratorUsername",
                    administratorUsername);
                builder.UseSetting(
                    "Authentication:BootstrapAdministratorPassword",
                    administratorPassword);
                builder.UseSetting(
                    "Authentication:DataProtectionKeysPath",
                    Path.Combine(directory, "keys"));
                if (databaseKeyRing)
                {
                    builder.UseSetting(
                        "Authentication:DataProtectionKeyPersistence",
                        nameof(DataProtectionKeyPersistence.Database));
                    if (rootKey is not null)
                    {
                        builder.UseSetting("Authentication:DataProtectionRootKey", rootKey);
                    }
                }
                builder.UseSetting("Storage:Provider", "Local");
                builder.UseSetting("Storage:RootPath", Path.Combine(directory, "storage"));
                builder.UseSetting("Database:Provider", "Sqlite");
                if (pinBusinessDatabase)
                {
                    builder.UseSetting(
                        "Database:ConnectionString",
                        $"Data Source={Path.Combine(directory, "structadoc.db")};Pooling=False");
                    builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
                }
                builder.UseSetting("ControlPlane:DatabasePath", controlPlanePath);
                if (fakeProtector)
                {
                    builder.ConfigureServices((context, services) =>
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
                    });
                }
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing)
                {
                    SqliteConnection.ClearAllPools();
                }
            }
        }
    }
}
