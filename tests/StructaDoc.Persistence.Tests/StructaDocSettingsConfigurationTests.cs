using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using StructaDoc.Application.Settings;
using StructaDoc.Infrastructure.ControlPlane;

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

        var configuration = StructaDocSettingsConfiguration.Create(shipped, options, []);

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
        var configuration = StructaDocSettingsConfiguration.Create(
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
        var configuration = StructaDocSettingsConfiguration.Create(shipped, options, []);

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

        var configuration = StructaDocSettingsConfiguration.Create(shipped, options, []);

        Assert.Equal("1", configuration.Effective[SettingCatalog.ParseMaxConcurrency]);
        Assert.False(File.Exists(options.DatabasePath));
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
