using StructaDoc.Application.Documents;
using StructaDoc.Application.Settings;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Storage;

namespace StructaDoc.ArchitectureTests;

/// <summary>
/// The catalog restates defaults that live in the options classes, because several settable keys are
/// absent from appsettings.json and would otherwise be reported as unset. A restatement that drifts
/// tells administrators the service is doing something it is not.
/// </summary>
public sealed class SettingCatalogContractTests
{
    [Fact]
    public void Document_defaults_match_the_options_they_describe()
    {
        var options = new DocumentIngestionOptions();

        Assert.Equal(
            options.UploadApiEnabled ? "true" : "false",
            Definition(SettingCatalog.UploadApiEnabled).Default);
        Assert.Equal(
            options.MaxUploadBytes.ToString(),
            Definition(SettingCatalog.MaxUploadBytes).Default);
    }

    [Fact]
    public void Identity_provider_defaults_match_the_options_they_describe()
    {
        var options = new OidcAuthenticationOptions();

        Assert.Equal(
            options.Enabled ? "true" : "false",
            Definition(SettingCatalog.OidcEnabled).Default);
        Assert.Equal(
            options.RequireHttpsMetadata ? "true" : "false",
            Definition(SettingCatalog.OidcRequireHttpsMetadata).Default);
        Assert.Equal(options.NameClaim, Definition(SettingCatalog.OidcNameClaim).Default);
        Assert.Equal(options.EmailClaim, Definition(SettingCatalog.OidcEmailClaim).Default);
        Assert.Equal(options.RoleClaim, Definition(SettingCatalog.OidcRoleClaim).Default);
        Assert.Equal(
            options.AdministratorRole,
            Definition(SettingCatalog.OidcAdministratorRole).Default);
    }

    [Fact]
    public void Storage_defaults_match_the_options_they_describe()
    {
        var options = new FileStorageOptions();

        Assert.Equal(options.Provider, Definition(SettingCatalog.StorageProvider).Default);
        Assert.Equal(options.RootPath, Definition(SettingCatalog.StorageRootPath).Default);
        Assert.Equal(options.Prefix, Definition(SettingCatalog.StoragePrefix).Default);
        Assert.Equal(
            options.ForcePathStyle ? "true" : "false",
            Definition(SettingCatalog.StorageForcePathStyle).Default);
    }

    [Fact]
    public void Database_defaults_match_the_options_they_describe()
    {
        var options = new DatabaseOptions();

        Assert.Equal(
            options.Provider.ToString(),
            Definition(SettingCatalog.DatabaseProvider).Default);

        // The connection string is the one default that is deliberately not restated. It is a secret,
        // so the catalog default would be a credential compiled into the image, and its real value
        // comes from the configuration the build ships rather than from this table.
        Assert.Equal(string.Empty, Definition(SettingCatalog.DatabaseConnectionString).Default);
    }

    [Fact]
    public void Every_closed_set_lists_exactly_what_its_options_class_accepts()
    {
        // A choice the web interface offers that the options class then refuses would be a value an
        // administrator can save and never start again with.
        Assert.Equal(
            Enum.GetNames<DatabaseProvider>(),
            Definition(SettingCatalog.DatabaseProvider).AllowedValues);

        foreach (var provider in Definition(SettingCatalog.StorageProvider).AllowedValues!)
        {
            new FileStorageOptions { Provider = provider, Bucket = "probe" }.Validate();
        }
    }

    [Fact]
    public void Every_recoverable_section_is_one_the_catalog_can_write_to()
    {
        // A section listed as recoverable but not settable would describe a rescue for a value no
        // browser can produce, which is the only case that needs rescuing.
        foreach (var section in SettingCatalog.RecoverableSections)
        {
            Assert.Contains(
                SettingCatalog.All,
                definition => definition.Key.StartsWith(section + ":", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_default_is_a_value_its_own_setting_accepts()
    {
        foreach (var definition in SettingCatalog.All)
        {
            // An empty default is not a value; it says the service ships without one, which is the
            // honest description of an authority nothing supplies. Normalize refuses empty text for
            // the same reason, because writing one clears the setting rather than setting it.
            if (definition.Default.Length == 0)
            {
                continue;
            }

            Assert.Equal(
                definition.Default,
                SettingCatalog.Normalize(definition, definition.Default));
        }
    }

    [Fact]
    public void No_secret_ships_with_a_default()
    {
        foreach (var definition in SettingCatalog.All.Where(SettingCatalog.IsSecret))
        {
            // A default here would be a credential compiled into the image, identical in every
            // deployment that never changed it.
            Assert.Equal(string.Empty, definition.Default);
        }
    }

    [Fact]
    public void Nothing_that_locates_the_key_ring_is_settable()
    {
        // Authentication options are read from the raw configuration before the store is opened,
        // because the key ring they locate is what decrypts the stored secrets. A settable key in
        // that section would be read too early to have any effect, and would silently do nothing.
        Assert.DoesNotContain(
            SettingCatalog.All,
            definition => definition.Key.StartsWith(
                StructaDocAuthenticationOptions.SectionName + ":",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Keys_are_unique_so_a_lookup_cannot_be_ambiguous()
    {
        var keys = SettingCatalog.All.Select(definition => definition.Key).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    private static SettingDefinition Definition(string key)
    {
        return SettingCatalog.Find(key)
            ?? throw new InvalidOperationException($"'{key}' is missing from the catalog.");
    }
}
