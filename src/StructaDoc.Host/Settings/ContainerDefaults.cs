using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace StructaDoc.Host.Settings;

/// <summary>
/// The container image's own configuration, present only inside the image.
///
/// It says where <c>/data</c> is. That used to be environment variables, which made it impossible to
/// change from the browser: an environment variable pins a setting, and the administration page then
/// reports it as unchangeable and refuses to write it. Storage and the business database are meant to
/// be moved from the browser, so the image ships them as defaults instead.
/// </summary>
public static class ContainerDefaults
{
    public const string FileName = "appsettings.Container.json";

    /// <summary>
    /// Adds the file beneath the environment variables and command line rather than on top of them.
    /// The position is the whole point: a value passed to <c>docker run</c> has to keep winning, and
    /// a source appended at the end would quietly beat it. Everything already added stays below,
    /// because the image's answer for where <c>/data</c> is should beat the repository's development
    /// default.
    /// </summary>
    public static IConfigurationBuilder AddContainerDefaults(this IConfigurationBuilder configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var deploymentIndex = configuration.Sources
            .Select((source, position) => (source, position))
            .Where(item => item.source is EnvironmentVariablesConfigurationSource
                or CommandLineConfigurationSource)
            .Select(item => (int?)item.position)
            .Min() ?? configuration.Sources.Count;

        configuration.Sources.Insert(
            deploymentIndex,
            new JsonConfigurationSource
            {
                Path = FileName,
                Optional = true,
                ReloadOnChange = false,
            });

        return configuration;
    }
}
