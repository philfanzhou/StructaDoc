using Microsoft.Extensions.Configuration;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Tests;

/// <summary>
/// Where the image's own defaults sit in the configuration chain. This is the whole of what makes
/// storage and the business database movable from the browser: as environment variables they were
/// pins, which the administration page reports as unchangeable and refuses to write.
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
            Path.Combine(directory, ContainerDefaults.FileName),
            """{ "Storage": { "RootPath": "/data/storage" } }""");
    }

    [Fact]
    public void The_image_default_beats_what_the_repository_ships()
    {
        var configuration = Build(deploymentArguments: []);

        // Inside the image, /data is the answer, not the repository's development path.
        Assert.Equal("/data/storage", configuration["Storage:RootPath"]);
    }

    [Fact]
    public void Anything_the_deployment_passes_still_beats_the_image_default()
    {
        var configuration = Build(deploymentArguments: ["--Storage:RootPath=/mnt/pinned"]);

        // An operator managing configuration from outside the container keeps doing that. A source
        // appended at the end of the chain instead of inserted would silently take this over.
        Assert.Equal("/mnt/pinned", configuration["Storage:RootPath"]);
    }

    [Fact]
    public void A_deployment_without_the_file_is_unaffected()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(directory, "empty"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = "./data/storage",
            })
            .AddContainerDefaults()
            .Build();

        // The file is optional because it exists only in the image. An ordinary `dotnet run` and
        // every test must be untouched by it.
        Assert.Equal("./data/storage", configuration["Storage:RootPath"]);
    }

    private IConfigurationRoot Build(string[] deploymentArguments)
    {
        return new ConfigurationBuilder()
            .SetBasePath(directory)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = "./data/storage",
            })
            .AddEnvironmentVariables()
            .AddCommandLine(deploymentArguments)
            .AddContainerDefaults()
            .Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
