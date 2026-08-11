using System.Globalization;

namespace StructaDoc.Application.Settings;

public enum SettingKind
{
    Boolean,
    Integer,
    String,

    /// <summary>
    /// An absolute HTTP or HTTPS address. Trailing slashes are removed, because an OIDC authority
    /// written with one and the issuer the provider returns without one are the same address, and a
    /// deployment should not fail on that difference.
    /// </summary>
    Uri,

    /// <summary>
    /// A value that is encrypted at rest and never sent back to a browser. Only whether it is set is
    /// reported, so a stolen administration session cannot read a credential it did not write.
    /// </summary>
    Secret,
}

/// <summary>
/// One configuration key an administrator may set through the web interface.
/// <paramref name="RequiresRestart"/> records whether the running service reads the value again on
/// its own; the value is never guessed from the key. <paramref name="Default"/> is what applies when
/// nothing supplies the key, which is not the same as the value being unset: several of these keys
/// are absent from appsettings.json and get their default from the options class instead, so
/// reporting an empty value would misdescribe what the service is doing.
///
/// <paramref name="Minimum"/> and <paramref name="Maximum"/> bound an integer. For text kinds only
/// <paramref name="Maximum"/> applies, as a length limit.
///
/// <paramref name="AllowedValues"/> closes a text setting to a fixed set. A key whose wrong value
/// would stop the service is better refused at the moment it is written, while the administrator is
/// still looking at it, than at the next restart.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingKind Kind,
    bool RequiresRestart,
    string Default,
    long Minimum = 0,
    long Maximum = 0,
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>
/// Settings are an allowlist rather than free-form configuration writes. An administrator is already
/// privileged, but a key that reached the store without appearing here would change behaviour no
/// test covers, and would let one compromised session reach every corner of configuration, including
/// paths and credentials that were never meant to be reachable from a browser.
/// </summary>
public static class SettingCatalog
{
    public const string ParseMaxConcurrency = "Worker:MaxConcurrency";
    public const string UploadApiEnabled = "Documents:UploadApiEnabled";
    public const string MaxUploadBytes = "Documents:MaxUploadBytes";

    public const string OidcEnabled = "Oidc:Enabled";
    public const string OidcAuthority = "Oidc:Authority";
    public const string OidcClientId = "Oidc:ClientId";
    public const string OidcClientSecret = "Oidc:ClientSecret";
    public const string OidcRequireHttpsMetadata = "Oidc:RequireHttpsMetadata";
    public const string OidcNameClaim = "Oidc:NameClaim";
    public const string OidcEmailClaim = "Oidc:EmailClaim";
    public const string OidcRoleClaim = "Oidc:RoleClaim";
    public const string OidcAdministratorRole = "Oidc:AdministratorRole";

    public const string StorageProvider = "Storage:Provider";
    public const string StorageRootPath = "Storage:RootPath";
    public const string StorageServiceUrl = "Storage:ServiceUrl";
    public const string StorageRegion = "Storage:Region";
    public const string StorageBucket = "Storage:Bucket";
    public const string StoragePrefix = "Storage:Prefix";
    public const string StorageAccessKey = "Storage:AccessKey";
    public const string StorageSecretKey = "Storage:SecretKey";
    public const string StorageForcePathStyle = "Storage:ForcePathStyle";

    public const string DatabaseProvider = "Database:Provider";
    public const string DatabaseConnectionString = "Database:ConnectionString";
    public const string DatabaseServerVersion = "Database:ServerVersion";

    /// <summary>
    /// The section every setting that configures sign-in through an identity provider belongs to.
    /// A bad value here can only be corrected from the browser, so these keys are treated as
    /// recoverable at startup rather than fatal.
    /// </summary>
    public const string OidcSection = "Oidc";

    /// <summary>
    /// Where documents and results are kept. A wrong value here is recoverable in the same way as
    /// sign-in: the section is dropped at startup and the service runs on its shipped default, which
    /// keeps the administration area reachable to correct it from.
    /// </summary>
    public const string StorageSection = "Storage";

    /// <summary>
    /// The business database. It is recoverable for a stronger reason than the others: a deployment
    /// pointed at a database that is not there has nothing working except the control plane, and the
    /// control plane is exactly what the administration area runs on.
    /// </summary>
    public const string DatabaseSection = "Database";

    /// <summary>Sections a stored value may be dropped from rather than stopping the service.</summary>
    public static IReadOnlyList<string> RecoverableSections { get; } =
        [OidcSection, StorageSection, DatabaseSection];

    private const int ClaimNameMaximumLength = 255;
    private const int PathMaximumLength = 1024;

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
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

        // Sign-in through an identity provider is the only way an end user reaches the workspace, so
        // a deployment that cannot configure it from the browser has no users at all.
        new(OidcEnabled, SettingKind.Boolean, RequiresRestart: true, Default: "false"),
        new(OidcAuthority, SettingKind.Uri, RequiresRestart: true, Default: "", Maximum: 2048),
        new(OidcClientId, SettingKind.String, RequiresRestart: true, Default: "", Maximum: 512),
        // Shorter than the column it lands in, because encrypting expands it and the limit has to
        // hold for a secret made entirely of multi-byte characters.
        new(OidcClientSecret, SettingKind.Secret, RequiresRestart: true, Default: "", Maximum: 512),
        new(OidcRequireHttpsMetadata, SettingKind.Boolean, RequiresRestart: true, Default: "true"),
        new(
            OidcNameClaim,
            SettingKind.String,
            RequiresRestart: true,
            Default: "name",
            Maximum: ClaimNameMaximumLength),
        new(
            OidcEmailClaim,
            SettingKind.String,
            RequiresRestart: true,
            Default: "email",
            Maximum: ClaimNameMaximumLength),
        new(
            OidcRoleClaim,
            SettingKind.String,
            RequiresRestart: true,
            Default: "role",
            Maximum: ClaimNameMaximumLength),
        new(
            OidcAdministratorRole,
            SettingKind.String,
            RequiresRestart: true,
            Default: "structadoc-admin",
            Maximum: ClaimNameMaximumLength),

        // Where uploads, results, and exports are kept. Moving a deployment to object storage
        // otherwise means recreating the container, which is the one thing this product's operator
        // is not expected to be able to do.
        new(
            StorageProvider,
            SettingKind.String,
            RequiresRestart: true,
            Default: "Local",
            Maximum: 16,
            AllowedValues: ["Local", "S3"]),
        new(
            StorageRootPath,
            SettingKind.String,
            RequiresRestart: true,
            Default: "./data/storage",
            Maximum: PathMaximumLength),
        new(StorageServiceUrl, SettingKind.Uri, RequiresRestart: true, Default: "", Maximum: 2048),
        new(StorageRegion, SettingKind.String, RequiresRestart: true, Default: "", Maximum: 64),
        new(StorageBucket, SettingKind.String, RequiresRestart: true, Default: "", Maximum: 255),
        new(
            StoragePrefix,
            SettingKind.String,
            RequiresRestart: true,
            Default: "structadoc",
            Maximum: 255),
        // Both halves are secrets. An access key identifies the credential rather than being the
        // whole of it, but sending storage credentials to a browser is a line this product does not
        // cross, and reporting whether one is set is all an administrator needs to manage it.
        new(StorageAccessKey, SettingKind.Secret, RequiresRestart: true, Default: "", Maximum: 512),
        new(StorageSecretKey, SettingKind.Secret, RequiresRestart: true, Default: "", Maximum: 512),
        new(StorageForcePathStyle, SettingKind.Boolean, RequiresRestart: true, Default: "true"),

        // The business database. Its connection string is a secret because it usually carries a
        // password, so it is written and never read back, exactly like a client secret.
        new(
            DatabaseProvider,
            SettingKind.String,
            RequiresRestart: true,
            Default: "Sqlite",
            Maximum: 16,
            AllowedValues: ["Sqlite", "PostgreSql", "MySql", "MariaDb"]),
        new(
            DatabaseConnectionString,
            SettingKind.Secret,
            RequiresRestart: true,
            Default: "",
            Maximum: 2048),
        new(
            DatabaseServerVersion,
            SettingKind.String,
            RequiresRestart: true,
            Default: "",
            Maximum: 32),
    ];

    /// <summary>
    /// True for settings whose value must never leave the service. Callers that read settings have to
    /// ask rather than infer it from the key, so adding a secret cannot forget to hide it.
    /// </summary>
    public static bool IsSecret(SettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Kind == SettingKind.Secret;
    }

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

            case SettingKind.String:
            case SettingKind.Secret:
                if (!IsAcceptableText(trimmed, definition.Maximum))
                {
                    return null;
                }

                // A closed set answers in its own spelling, so what is stored is what the options
                // class parses rather than whatever casing was typed.
                return definition.AllowedValues is null
                    ? trimmed
                    : definition.AllowedValues.FirstOrDefault(
                        allowed => string.Equals(allowed, trimmed, StringComparison.OrdinalIgnoreCase));

            case SettingKind.Uri:
                if (!IsAcceptableText(trimmed, definition.Maximum))
                {
                    return null;
                }

                // Trailing slashes are removed so the stored address matches the issuer an identity
                // provider reports, which is the difference most misconfigurations come down to.
                var address = trimmed.TrimEnd('/');
                return Uri.TryCreate(address, UriKind.Absolute, out var parsed)
                    && parsed.Scheme is "http" or "https"
                        ? address
                        : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Control characters are refused because these values are written into configuration, headers,
    /// and log lines, where a newline turns one value into two.
    /// </summary>
    private static bool IsAcceptableText(string value, long maximumLength)
    {
        return value.Length <= maximumLength && !value.Any(char.IsControl);
    }
}
