using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using StructaDoc.Application.Settings;
using StructaDoc.Adapters.ControlPlane;

namespace StructaDoc.Persistence.Tests;

/// <summary>
/// Precedence between what a deployment pins and what an administrator chose is the whole point of
/// the settings store, so it is tested where the rule lives rather than only through the endpoints.
/// </summary>
public sealed class StructaDocSettingsConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "structadoc-settings-configuration",
        Guid.NewGuid().ToString("N"));

    private readonly FakeSettingSecretProtector protector = new();
    private readonly SettingsStartupFault fault = new();

    public StructaDocSettingsConfigurationTests()
    {
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void A_stored_setting_beats_the_shipped_default()
    {
        var options = CreateStore(new Dictionary<string, string>
        {
            [SettingCatalog.ParseMaxConcurrency] = "6",
        });
        var shipped = Configuration(new() { [SettingCatalog.ParseMaxConcurrency] = "1" });

        var configuration = Create(shipped, options, []);

        Assert.Equal("6", configuration.Effective[SettingCatalog.ParseMaxConcurrency]);
        Assert.False(configuration.IsManagedExternally(SettingCatalog.ParseMaxConcurrency));
    }

    [Fact]
    public void A_value_the_deployment_pins_beats_a_stored_setting()
    {
        var options = CreateStore(new Dictionary<string, string>
        {
            [SettingCatalog.ParseMaxConcurrency] = "6",
        });
        // A pinned value is already part of the application configuration, exactly as an environment
        // variable or command-line argument would be. Passing the arguments again is how the pin is
        // recognised as one, not how it gets its value.
        var pinned = Configuration(new() { [SettingCatalog.ParseMaxConcurrency] = "9" });

        // The stored value is left out of the top layer rather than overridden by it, so the pinned
        // value keeps winning no matter where a host builder places its own sources.
        var configuration = Create(
            pinned,
            options,
            ["--Worker:MaxConcurrency=9"]);

        Assert.Equal("9", configuration.Effective[SettingCatalog.ParseMaxConcurrency]);
        Assert.True(configuration.IsManagedExternally(SettingCatalog.ParseMaxConcurrency));
    }

    [Fact]
    public void Stored_keys_outside_the_catalog_are_ignored()
    {
        var options = CreateStore(new Dictionary<string, string>
        {
            ["Database:ConnectionString"] = "Data Source=elsewhere.db",
        });
        var shipped = Configuration(new() { ["Database:ConnectionString"] = "Data Source=real.db" });

        // An upgrade that stops publishing a key must stop honouring rows that still hold it.
        var configuration = Create(shipped, options, []);

        Assert.Equal("Data Source=real.db", configuration.Effective["Database:ConnectionString"]);
    }

    [Fact]
    public void A_control_plane_that_does_not_exist_yet_leaves_configuration_untouched()
    {
        // Settings are read before the control-plane migrations run, so the first start of a new
        // deployment must be an ordinary case rather than a failure.
        var options = new ControlPlaneOptions
        {
            DatabasePath = Path.Combine(directory, "missing.db"),
        };
        var shipped = Configuration(new() { [SettingCatalog.ParseMaxConcurrency] = "1" });

        var configuration = Create(shipped, options, []);

        Assert.Equal("1", configuration.Effective[SettingCatalog.ParseMaxConcurrency]);
        Assert.False(File.Exists(options.DatabasePath));
    }

    [Fact]
    public void A_stored_secret_reaches_configuration_decrypted()
    {
        var options = CreateStore(new Dictionary<string, string>
        {
            [SettingCatalog.OidcClientSecret] =
                FakeSettingSecretProtector.Prefix + "the-client-secret",
        });

        var configuration = Create(Configuration(new()), options, []);

        // What the row holds and what the service reads are deliberately different. A secret that
        // arrived still encrypted would be sent to the identity provider as the client secret.
        Assert.Equal("the-client-secret", configuration.Effective[SettingCatalog.OidcClientSecret]);
        Assert.False(fault.HasFault);
    }

    [Fact]
    public void A_secret_that_cannot_be_decrypted_is_dropped_and_reported()
    {
        // What a deployment sees after losing or replacing /data/keys.
        var options = CreateStore(new Dictionary<string, string>
        {
            [SettingCatalog.OidcClientSecret] = "written-by-a-key-ring-that-is-gone",
        });

        var configuration = Create(Configuration(new()), options, []);

        // Starting without the secret is the only option left, and saying so is what turns an
        // unexplained sign-in failure into something an administrator can act on.
        Assert.Null(configuration.Effective[SettingCatalog.OidcClientSecret]);
        Assert.True(fault.HasFault);
        Assert.Equal(SettingCatalog.OidcSection, fault.Section);
        Assert.Contains(SettingCatalog.OidcClientSecret, fault.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_is_stored_only_when_a_value_in_it_came_from_the_store()
    {
        var options = CreateStore(new Dictionary<string, string>
        {
            [SettingCatalog.OidcAuthority] = "https://issuer.example",
        });

        var configuration = Create(Configuration(new()), options, []);

        Assert.True(configuration.IsStoredSection(SettingCatalog.OidcSection));
        Assert.False(configuration.IsStoredSection("Worker"));
    }

    [Fact]
    public void A_pinned_value_does_not_make_its_section_stored()
    {
        var options = CreateStore(new Dictionary<string, string>
        {
            [SettingCatalog.OidcAuthority] = "https://stored.example",
        });
        var pinned = Configuration(new()
        {
            [SettingCatalog.OidcAuthority] = "https://pinned.example",
        });

        var configuration = Create(pinned, options, ["--Oidc:Authority=https://pinned.example"]);

        // The row is dead weight next to the pin, so nothing in this section came from the store.
        // Treating it as stored would make a value only the command line can fix look recoverable
        // from the browser, and the service would start with sign-in quietly switched off.
        Assert.False(configuration.IsStoredSection(SettingCatalog.OidcSection));
    }

    [Fact]
    public void Dropping_a_stored_section_keeps_stored_values_from_other_sections()
    {
        var options = CreateStore(new Dictionary<string, string>
        {
            [SettingCatalog.OidcAuthority] = "https://issuer.example",
            [SettingCatalog.ParseMaxConcurrency] = "6",
        });

        var reduced = Create(Configuration(new()), options, [])
            .WithoutStoredSection(SettingCatalog.OidcSection);

        // Rejecting one section must not quietly undo every other choice an administrator made.
        Assert.Null(reduced[SettingCatalog.OidcAuthority]);
        Assert.Equal("6", reduced[SettingCatalog.ParseMaxConcurrency]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private StructaDocSettingsConfiguration Create(
        IConfiguration shipped,
        ControlPlaneOptions options,
        string[] commandLineArguments)
    {
        return StructaDocSettingsConfiguration.Create(
            shipped,
            options,
            commandLineArguments,
            protector,
            fault);
    }

    private ControlPlaneOptions CreateStore(Dictionary<string, string> settings)
    {
        var options = new ControlPlaneOptions
        {
            DatabasePath = Path.Combine(directory, $"{Guid.NewGuid():N}.db"),
        };

        using var connection = new SqliteConnection(options.ConnectionString);
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, "
                + "updated_at_utc TEXT NOT NULL, updated_by TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        foreach (var (key, value) in settings)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO settings (key, value, updated_at_utc, updated_by) "
                + "VALUES ($key, $value, '2026-08-08T00:00:00', 'test')";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }

        return options;
    }
}


