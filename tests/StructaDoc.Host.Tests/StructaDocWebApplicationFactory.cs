using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StructaDoc.Host.Tests;

public sealed class StructaDocWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdministratorEmail = "admin@structadoc.test";
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
        builder.UseSetting("Documents:UploadApiEnabled", "true");
        builder.UseSetting("Documents:MaxUploadBytes", "1048576");
        builder.UseSetting(
            "Authentication:DataProtectionKeysPath",
            Path.Combine(testDirectory, "keys"));
        builder.UseSetting(
            "Authentication:BootstrapAdministratorEmail",
            AdministratorEmail);
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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}
