using Microsoft.Extensions.Configuration;

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
    /// Applies the file above everything the deployment did not supply, and not at all for the keys
    /// it did.
    ///
    /// Precedence is decided by key rather than by where the file lands in the source list, for the
    /// same reason <see cref="StructaDoc.Adapters.ControlPlane.StructaDocSettingsConfiguration"/>
    /// decides it that way: host builders do not agree on that order. The web host reads environment
    /// variables both before and after <c>appsettings.json</c> and then chains its host configuration
    /// on at the end, so no single position is above the repository's defaults and below the
    /// deployment's at once. Placing the file by source type produced exactly that: it landed under
    /// <c>appsettings.json</c>, and the image started against a read-only <c>/app/data</c>.
    /// </summary>
    public static IConfigurationBuilder AddContainerDefaults(
        this IConfigurationBuilder configuration,
        string[] commandLineArguments)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The file exists only in the image, so an ordinary `dotnet run` and every test must be
        // untouched by it. It is read through the builder's own file provider, which is what makes
        // it resolve against the content root the same way `appsettings.json` does.
        var image = new ConfigurationBuilder()
            .SetFileProvider(configuration.GetFileProvider())
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .Build();

        var deployment = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(commandLineArguments ?? [])
            .Build();

        var defaults = image
            .AsEnumerable()
            .Where(entry => entry.Value is not null && deployment[entry.Key] is null)
            .ToArray();

        if (defaults.Length > 0)
        {
            configuration.AddInMemoryCollection(defaults);
        }

        return configuration;
    }
}
