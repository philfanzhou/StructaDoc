using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using StructaDoc.Application.Settings;

namespace StructaDoc.Adapters.ControlPlane;

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
    private readonly HashSet<string> storedKeys;

    private StructaDocSettingsConfiguration(
        IConfiguration effective,
        IConfiguration baseConfiguration,
        IConfiguration deployment,
        HashSet<string> storedKeys)
    {
        Effective = effective;
        Base = baseConfiguration;
        this.deployment = deployment;
        this.storedKeys = storedKeys;
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
        string[] commandLineArguments,
        ISettingSecretProtector secretProtector,
        SettingsStartupFault fault)
    {
        ArgumentNullException.ThrowIfNull(baseConfiguration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secretProtector);
        ArgumentNullException.ThrowIfNull(fault);

        var deployment = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(commandLineArguments ?? [])
            .Build();

        var stored = Load(options, secretProtector, fault)
            .Where(setting => deployment[setting.Key] is null)
            .ToArray();

        var storedKeys = stored
            .Select(setting => setting.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (stored.Length == 0)
        {
            return new StructaDocSettingsConfiguration(
                baseConfiguration,
                baseConfiguration,
                deployment,
                storedKeys);
        }

        var effective = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(stored)
            .Build();

        return new StructaDocSettingsConfiguration(
            effective,
            baseConfiguration,
            deployment,
            storedKeys);
    }

    /// <summary>
    /// True when the deployment pins this key, which is what makes a setting read-only in the web
    /// interface: writing it would store a value the service never reads.
    /// </summary>
    public bool IsManagedExternally(string key)
    {
        return deployment[key] is not null;
    }

    /// <summary>
    /// True when any value in this configuration section came from the store rather than from the
    /// deployment. It decides whether a section that fails validation can be dropped so the service
    /// still starts: a value written from the browser can be corrected from the browser, whereas a
    /// pinned one can only be corrected by whoever has the command line, who is better served by the
    /// service refusing to start.
    /// </summary>
    public bool IsStoredSection(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);

        var prefix = section + ":";
        return storedKeys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Configuration with every stored value from this section removed, which is what the service
    /// binds after rejecting one. Nothing from the section is kept: a client id without the authority
    /// that was refused alongside it is not a configuration anyone chose.
    /// </summary>
    public IConfiguration WithoutStoredSection(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);

        if (!IsStoredSection(section))
        {
            return Effective;
        }

        var prefix = section + ":";
        var retained = storedKeys
            .Where(key => !key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(key => new KeyValuePair<string, string?>(key, Effective[key]))
            .ToArray();

        return new ConfigurationBuilder()
            .AddConfiguration(Base)
            .AddInMemoryCollection(retained)
            .Build();
    }

    private static IEnumerable<KeyValuePair<string, string?>> Load(
        ControlPlaneOptions options,
        ISettingSecretProtector secretProtector,
        SettingsStartupFault fault)
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
            var definition = SettingCatalog.Find(key);
            if (definition is null || reader.IsDBNull(1))
            {
                continue;
            }

            var value = reader.GetString(1);
            if (SettingCatalog.IsSecret(definition))
            {
                value = secretProtector.TryUnprotect(value)!;
                if (value is null)
                {
                    // The key ring that encrypted this is gone. Starting without the secret is the
                    // only option left, and saying so is what turns an unexplained sign-in failure
                    // into something an administrator can act on.
                    fault.Record(
                        SectionOf(key),
                        $"'{key}' could not be decrypted with the current Data Protection key ring and was ignored. Set it again.");
                    continue;
                }
            }

            yield return new KeyValuePair<string, string?>(key, value);
        }
    }

    private static string SectionOf(string key)
    {
        var separator = key.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 ? key[..separator] : key;
    }
}
