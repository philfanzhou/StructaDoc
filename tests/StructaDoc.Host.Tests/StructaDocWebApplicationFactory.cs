using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace StructaDoc.Host.Tests;

public sealed class StructaDocWebApplicationFactory : WebApplicationFactory<Program>
{
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
        builder.UseSetting("Documents:UploadApiEnabled", "true");
        builder.UseSetting("Documents:MaxUploadBytes", "1048576");
        builder.UseSetting("Storage:Provider", "Local");
        builder.UseSetting(
            "Storage:RootPath",
            StorageRootPath);

        builder.ConfigureAppConfiguration(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:ConnectionString"] =
                        $"Data Source={Path.Combine(testDirectory, "structadoc.db")};Pooling=False",
                    ["Database:ApplyMigrationsOnStartup"] = "true",
                }));
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
