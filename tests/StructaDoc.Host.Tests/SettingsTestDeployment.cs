using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Tests;

/// <summary>
/// One directory that outlives the Host, so a settings store can be observed across a restart rather
/// than only within the process that wrote it. Several settings behaviours only exist between two
/// processes, which is why this is shared rather than private to one test class.
/// </summary>
internal sealed class SettingsTestDeployment : IDisposable
{
    public const string Username = "settings-admin";
    public const string Password = "StructaDoc-Settings-2026!";

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "structadoc-settings-tests",
        Guid.NewGuid().ToString("N"));

    public SettingsTestDeployment()
    {
        Directory.CreateDirectory(directory);
    }

    public string ControlPlanePath => Path.Combine(directory, "control.db");

    /// <param name="pinBusinessDatabase">
    /// The test host passes every <c>UseSetting</c> value to the entry point as a command-line
    /// argument, which is a pin, so a key set that way beats anything stored. Leaving the connection
    /// string out is how a test reaches the case a real deployment is in: one an administrator chose
    /// from the browser.
    /// </param>
    public WebApplicationFactory<Program> CreateFactory(
        Action<IWebHostBuilder>? configure = null,
        bool pinBusinessDatabase = true)
    {
        return new SettingsTestFactory(directory, configure, pinBusinessDatabase);
    }

    public string BusinessDatabasePath => Path.Combine(directory, "structadoc.db");

    public static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var anonymousToken = await client.GetAntiforgeryTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
        {
            Content = JsonContent.Create(new AdministratorLoginRequest(Username, Password)),
        };
        request.Headers.Add(anonymousToken.HeaderName, anonymousToken.RequestToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var token = await client.GetAntiforgeryTokenAsync();
        client.DefaultRequestHeaders.Remove(token.HeaderName);
        client.DefaultRequestHeaders.Add(token.HeaderName, token.RequestToken);
        return client;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SettingsTestFactory(
        string directory,
        Action<IWebHostBuilder>? configure,
        bool pinBusinessDatabase = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Worker:Enabled", "false");
            builder.UseSetting("Authentication:BootstrapAdministratorUsername", Username);
            builder.UseSetting("Authentication:BootstrapAdministratorPassword", Password);
            builder.UseSetting(
                "Authentication:DataProtectionKeysPath",
                Path.Combine(directory, "keys"));
            builder.UseSetting("Storage:Provider", "Local");
            builder.UseSetting("Storage:RootPath", Path.Combine(directory, "storage"));
            builder.UseSetting("Database:Provider", "Sqlite");
            if (pinBusinessDatabase)
            {
                builder.UseSetting(
                    "Database:ConnectionString",
                    $"Data Source={Path.Combine(directory, "structadoc.db")};Pooling=False");
            }
            builder.UseSetting("ControlPlane:DatabasePath", Path.Combine(directory, "control.db"));
            configure?.Invoke(builder);
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
