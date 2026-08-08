using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using StructaDoc.Application.Settings;

namespace StructaDoc.Infrastructure.ControlPlane;

/// <summary>
/// Configuration as the service actually sees it, once settings an administrator chose in the
/// browser are taken into account.
///
/// Precedence is decided here rather than by where a configuration source lands in a list. Host
/// builders do not agree on that order, and the test host appends sources after the application has
/// already configured itself, so an ordering-based rule would mean one thing in production and
/// another under test. Stored settings are layered on top of everything instead, and any key the
/// deployment pins through an environment variable or the command line is left out of that layer
/// entirely: it keeps winning because nothing was ever put above it.
///
/// Environment variables and the command line are read separately only to recognise which keys are
/// pinned. Their values reach the service through the application configuration as usual.
/// </summary>
public sealed class StructaDocSettingsConfiguration
{
    private readonly IConfiguration deployment;

    private StructaDocSettingsConfiguration(
        IConfiguration effective,
        IConfiguration baseConfiguration,
        IConfiguration deployment)
    {
        Effective = effective;
        Base = baseConfiguration;
        this.deployment = deployment;
    }

    /// <summary>
    /// Configuration including the stored settings that existed when this process started, which is
    /// what its options were bound from.
    /// </summary>
    public IConfiguration Effective { get; }

    /// <summary>
    /// Configuration without any stored setting. Reporting what applies to a key with no stored row
    /// has to come from here: <see cref="Effective"/> still holds the value a row had at startup,
    /// so a row deleted since then would otherwise be reported as if it were still in force.
    /// </summary>
    public IConfiguration Base { get; }

    public static StructaDocSettingsConfiguration Create(
        IConfiguration baseConfiguration,
        ControlPlaneOptions options,
        string[] commandLineArguments)
    {
        ArgumentNullException.ThrowIfNull(baseConfiguration);
        ArgumentNullException.ThrowIfNull(options);

        var deployment = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(commandLineArguments ?? [])
            .Build();

        var stored = Load(options)
            .Where(setting => deployment[setting.Key] is null)
            .ToArray();

        if (stored.Length == 0)
        {
            return new StructaDocSettingsConfiguration(
                baseConfiguration,
                baseConfiguration,
                deployment);
        }

        var effective = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(stored)
            .Build();

        return new StructaDocSettingsConfiguration(effective, baseConfiguration, deployment);
    }

    /// <summary>
    /// True when the deployment pins this key, which is what makes a setting read-only in the web
    /// interface: writing it would store a value the service never reads.
    /// </summary>
    public bool IsManagedExternally(string key)
    {
        return deployment[key] is not null;
    }

    private static IEnumerable<KeyValuePair<string, string?>> Load(ControlPlaneOptions options)
    {
        // Settings are read before the control-plane migrations run, so on a first start neither the
        // file nor the table exists yet. That is not an error: a deployment with no stored settings
        // and one that has not been migrated both mean the shipped defaults apply. The file is
        // deliberately not created here, because creating it is the migration's job.
        if (!File.Exists(options.DatabasePath))
        {
            yield break;
        }

        using var connection = new SqliteConnection(options.ConnectionString);
        connection.Open();

        using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'settings'";
            if (probe.ExecuteScalar() is null)
            {
                yield break;
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var key = reader.GetString(0);

            // A key that is no longer settable must not keep steering the service after an upgrade
            // removed it from the catalog.
            if (SettingCatalog.Find(key) is not null && !reader.IsDBNull(1))
            {
                yield return new KeyValuePair<string, string?>(key, reader.GetString(1));
            }
        }
    }
}
