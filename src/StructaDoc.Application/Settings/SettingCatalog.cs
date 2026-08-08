using System.Globalization;

namespace StructaDoc.Application.Settings;

public enum SettingKind
{
    Boolean,
    Integer,
}

/// <summary>
/// One configuration key an administrator may set through the web interface.
/// <paramref name="RequiresRestart"/> records whether the running service reads the value again on
/// its own; the value is never guessed from the key. <paramref name="Default"/> is what applies when
/// nothing supplies the key, which is not the same as the value being unset: several of these keys
/// are absent from appsettings.json and get their default from the options class instead, so
/// reporting an empty value would misdescribe what the service is doing.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingKind Kind,
    bool RequiresRestart,
    string Default,
    long Minimum = 0,
    long Maximum = 0);

/// <summary>
/// Settings are an allowlist rather than free-form configuration writes. An administrator is already
/// privileged, but a key that reached the store without appearing here would change behaviour no
/// test covers, and would let one compromised session reach every corner of configuration, including
/// paths and credentials that were never meant to be reachable from a browser.
/// </summary>
public static class SettingCatalog
{
    public const string ParseExecutionEnabled = "Worker:ExecutionEnabled";
    public const string ParseMaxConcurrency = "Worker:MaxConcurrency";
    public const string UploadApiEnabled = "Documents:UploadApiEnabled";
    public const string MaxUploadBytes = "Documents:MaxUploadBytes";

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        // The one setting the running service re-reads, because turning parsing on and off is a
        // routine act rather than a deployment change.
        new(ParseExecutionEnabled, SettingKind.Boolean, RequiresRestart: false, Default: "false"),
        new(
            ParseMaxConcurrency,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "1",
            Minimum: 1,
            Maximum: 64),
        new(UploadApiEnabled, SettingKind.Boolean, RequiresRestart: true, Default: "false"),
        new(
            MaxUploadBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "104857600",
            Minimum: 1024,
            Maximum: 8L * 1024 * 1024 * 1024),
    ];

    public static SettingDefinition? Find(string? key)
    {
        return All.SingleOrDefault(
            definition => string.Equals(definition.Key, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Converts a submitted value to the exact text the configuration system will parse back, or
    /// returns <see langword="null"/> when it is not a value this setting accepts.
    /// </summary>
    public static string? Normalize(SettingDefinition definition, string? value)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        switch (definition.Kind)
        {
            case SettingKind.Boolean:
                return bool.TryParse(trimmed, out var flag)
                    ? flag ? "true" : "false"
                    : null;

            case SettingKind.Integer:
                if (!long.TryParse(
                        trimmed,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var number))
                {
                    return null;
                }

                return number >= definition.Minimum && number <= definition.Maximum
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : null;

            default:
                return null;
        }
    }
}
