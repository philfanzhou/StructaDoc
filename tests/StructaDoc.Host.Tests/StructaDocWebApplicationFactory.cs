using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace StructaDoc.Host.Tests;

public sealed class StructaDocWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdministratorUsername = "structadoc-admin";
    public const string AdministratorPassword = "StructaDoc-Test-Password-2026!";

    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "structadoc-host-tests",
        Guid.NewGuid().ToString("N"));

    public string StorageRootPath => Path.Combine(testDirectory, "storage");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(testDirectory);
        builder.UseSetting("Worker:Enabled", "true");
        builder.UseSetting("Worker:MaintenanceInterval", "00:00:00.100");
        builder.UseSetting("Worker:RecoveryBatchSize", "20");
        builder.UseSetting("Worker:LeaseDuration", "00:00:30");
        builder.UseSetting("Worker:HeartbeatInterval", "00:00:00.100");
        // Every test on this shared host signs in once, so the shipped limit is reached by adding a
        // test rather than by anything the tests are about. The limiter itself is covered against a
        // host configured for it in AdministratorSessionEndpointTests.
        builder.UseSetting("Authentication:LoginPermitLimit", "1000");
        builder.UseSetting("Documents:UploadApiEnabled", "true");
        builder.UseSetting("Documents:MaxUploadBytes", "1048576");
        builder.UseSetting(
            "Authentication:DataProtectionKeysPath",
            Path.Combine(testDirectory, "keys"));
        builder.UseSetting(
            "Authentication:BootstrapAdministratorUsername",
            AdministratorUsername);
        builder.UseSetting(
            "Authentication:BootstrapAdministratorPassword",
            AdministratorPassword);
        builder.UseSetting(
            "Authentication:BootstrapAdministratorDisplayName",
            "StructaDoc Test Administrator");
        builder.UseSetting("Storage:Provider", "Local");
        builder.UseSetting(
            "Storage:RootPath",
            StorageRootPath);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting(
            "Database:ConnectionString",
            $"Data Source={Path.Combine(testDirectory, "structadoc.db")};Pooling=False");
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting(
            "ControlPlane:DatabasePath",
            Path.Combine(testDirectory, "control.db"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(testDirectory))
        {
            // The control-plane database keeps pooling enabled, so its file stays open until the
            // pool is released. The business database opts out of pooling in its connection string.
            SqliteConnection.ClearAllPools();

            Directory.Delete(testDirectory, recursive: true);
        }
    }
}
