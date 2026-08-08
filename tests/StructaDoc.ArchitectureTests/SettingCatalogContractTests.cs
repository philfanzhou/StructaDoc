using StructaDoc.Application.Documents;
using StructaDoc.Application.Settings;

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
    public void Every_default_is_a_value_its_own_setting_accepts()
    {
        foreach (var definition in SettingCatalog.All)
        {
            Assert.Equal(
                definition.Default,
                SettingCatalog.Normalize(definition, definition.Default));
        }
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
