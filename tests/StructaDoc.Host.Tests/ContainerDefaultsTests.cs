using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Where the image's own defaults sit in the configuration chain. This is the whole of what makes
/// storage and the business database movable from the browser: as environment variables they were
/// pins, which the administration page reports as unchangeable and refuses to write.
///
/// Every test here goes through the builder the service actually starts from. An earlier version of
/// these tests assembled a configuration chain by hand, which proved the rule only against the order
/// the test itself chose: the web host reads environment variables both before and after
/// appsettings.json and chains its host configuration on at the end, and against that real order the
/// image started on the repository's development path and died on a read-only /app/data.
/// </summary>
public sealed class ContainerDefaultsTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"structadoc-container-defaults-{Guid.NewGuid():N}");

    public ContainerDefaultsTests()
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "empty"));
        File.WriteAllText(
            Path.Combine(directory, "appsettings.json"),
            """{ "Storage": { "RootPath": "./data/storage" } }""");
        File.WriteAllText(
            Path.Combine(directory, "empty", "appsettings.json"),
            """{ "Storage": { "RootPath": "./data/storage" } }""");
        File.WriteAllText(
            Path.Combine(directory, ContainerDefaults.FileName),
            """{ "Storage": { "RootPath": "/data/storage" } }""");
    }

    [Fact]
    public void The_image_default_beats_what_the_repository_ships()
    {
        var configuration = Build(directory, deploymentArguments: []);

        // Inside the image, /data is the answer, not the repository's development path.
        Assert.Equal("/data/storage", configuration["Storage:RootPath"]);
    }

    [Fact]
    public void Anything_the_deployment_passes_still_beats_the_image_default()
    {
        var configuration = Build(directory, ["--Storage:RootPath=/mnt/pinned"]);

        // An operator managing configuration from outside the container keeps doing that, and the
        // administration page keeps reporting the setting as unchangeable, because the image default
        // is not applied to a key the deployment supplied at all.
        Assert.Equal("/mnt/pinned", configuration["Storage:RootPath"]);
    }

    [Fact]
    public void A_deployment_without_the_file_is_unaffected()
    {
        var configuration = Build(Path.Combine(directory, "empty"), deploymentArguments: []);

        // The file is optional because it exists only in the image. An ordinary `dotnet run` and
        // every test must be untouched by it.
        Assert.Equal("./data/storage", configuration["Storage:RootPath"]);
    }

    private static IConfiguration Build(string contentRoot, string[] deploymentArguments)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            EnvironmentName = Environments.Production,
            Args = deploymentArguments,
        });

        builder.Configuration.AddContainerDefaults(deploymentArguments);
        return builder.Configuration;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
