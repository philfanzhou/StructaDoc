using StructaDoc.Application.Documents;
using StructaDoc.Application.Settings;
using StructaDoc.Adapters.Authentication;

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
