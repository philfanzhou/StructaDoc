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
    /// A time span in the format the .NET configuration binder reads, such as <c>00:00:30</c> or
    /// <c>1.00:00:00</c>. Stored values are rewritten to the invariant round-trippable spelling so
    /// that what the value reports tomorrow is what was written today.
    /// </summary>
    Duration,

    /// <summary>
    /// A number that may carry a fractional part, such as a compression ratio. Parsed and stored in
    /// the invariant culture only, so a value never depends on the server's regional settings.
    /// </summary>
    Number,

    /// <summary>
    /// A value that is encrypted at rest and never sent back to a browser. Only whether it is set is
    /// reported, so a stolen administration session cannot read a credential it did not write.
    /// </summary>
    Secret,
}

/// <summary>
/// One configuration key an administrator may set through the web interface.
/// <paramref name="RequiresRestart"/> classifies the key as one the running service does not pick
/// up on its own: options bind once at startup, so the catalog marks a key restart-class unless a
/// change listener owns it. Whether a restart is actually needed for a given write is answered from
/// what applied, not from this flag, and the flag is never guessed from the key.
/// <paramref name="Default"/> is what applies when
/// nothing supplies the key, which is not the same as the value being unset: several of these keys
/// are absent from appsettings.json and get their default from the options class instead, so
/// reporting an empty value would misdescribe what the service is doing.
///
/// <paramref name="Minimum"/> and <paramref name="Maximum"/> bound an integer. A
/// <see cref="SettingKind.Duration"/> is bounded in whole milliseconds. A
/// <see cref="SettingKind.Number"/> is bounded by whole-number comparisons. For those three kinds a
/// <paramref name="Maximum"/> of zero means no upper bound, because the validation behind the key
/// imposes none; for text kinds only <paramref name="Maximum"/> applies, as a length limit.
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
    public const string WorkerMaintenanceInterval = "Worker:MaintenanceInterval";
    public const string WorkerRecoveryBatchSize = "Worker:RecoveryBatchSize";
    public const string WorkerLeaseDuration = "Worker:LeaseDuration";
    public const string WorkerHeartbeatInterval = "Worker:HeartbeatInterval";
    public const string WorkerRetryDelay = "Worker:RetryDelay";
    public const string WorkerMinimumPollDelay = "Worker:MinimumPollDelay";
    public const string WorkerMaximumPollDelay = "Worker:MaximumPollDelay";
    public const string WorkerMaxExecutionDuration = "Worker:MaxExecutionDuration";
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

    public const string AuthenticationAdministratorSessionLifetime =
        "Authentication:AdministratorSessionLifetime";
    public const string AuthenticationLoginPermitLimit = "Authentication:LoginPermitLimit";
    public const string AuthenticationLoginRateLimitWindow = "Authentication:LoginRateLimitWindow";

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

    public const string ProviderResultsMaxArchiveBytes = "ProviderResults:MaxArchiveBytes";
    public const string ProviderResultsMaxEntryCount = "ProviderResults:MaxEntryCount";
    public const string ProviderResultsMaxEntryBytes = "ProviderResults:MaxEntryBytes";
    public const string ProviderResultsMaxExpandedBytes = "ProviderResults:MaxExpandedBytes";
    public const string ProviderResultsMaxCompressionRatio = "ProviderResults:MaxCompressionRatio";
    public const string ProviderResultsMaxEntryPathBytes = "ProviderResults:MaxEntryPathBytes";
    public const string ProviderResultsMaxCentralDirectoryBytes =
        "ProviderResults:MaxCentralDirectoryBytes";

    public const string ProviderResultNormalizationMaxMarkdownBytes =
        "ProviderResultNormalization:MaxMarkdownBytes";
    public const string ProviderResultNormalizationMaxJsonBytes =
        "ProviderResultNormalization:MaxJsonBytes";
    public const string ProviderResultNormalizationMaxAssetBytes =
        "ProviderResultNormalization:MaxAssetBytes";

    public const string LibreOfficeEnabled = "LibreOffice:Enabled";
    public const string LibreOfficeMaxConcurrency = "LibreOffice:MaxConcurrency";
    public const string LibreOfficeTimeout = "LibreOffice:Timeout";
    public const string LibreOfficeResourceInspectionInterval =
        "LibreOffice:ResourceInspectionInterval";
    public const string LibreOfficeMaxInputBytes = "LibreOffice:MaxInputBytes";
    public const string LibreOfficeMaxOutputBytes = "LibreOffice:MaxOutputBytes";
    public const string LibreOfficeMaxTemporaryBytes = "LibreOffice:MaxTemporaryBytes";

    /// <summary>
    /// The section every setting that configures sign-in through an identity provider belongs to.
    /// A bad value here can only be corrected from the browser, so these keys are treated as
    /// recoverable at startup rather than fatal.
    /// </summary>
    public const string OidcSection = "Oidc";

    /// <summary>
    /// The tuning half of local administration sign-in: how long an administrator session lasts and
    /// how tightly the two sign-in endpoints are rate limited. It is recoverable for the same reason
    /// as the identity-provider section — the browser that wrote a wrong value is the only surface it
    /// can be corrected from — while the other half of the same section (the key ring and bootstrap
    /// credentials) stays out of the catalog entirely because it is read before the store opens.
    /// </summary>
    public const string AuthenticationSection = "Authentication";

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

    /// <summary>
    /// The Parse Run Worker's timing and batch tuning. Its keys are pure adjustment — wrong values
    /// slow parsing down or hold leases too long but leave the administration area working — so a
    /// stored combination that fails validation (a heartbeat no shorter than its lease, for example)
    /// is dropped as a whole rather than stopping the service.
    /// </summary>
    public const string WorkerSection = "Worker";

    /// <summary>
    /// Bounds on the Provider result archive an attempt will accept. Recoverable for the same reason
    /// as the Worker section: a stored combination that contradicts itself is corrected from the
    /// browser, and the parsing limits it leaves behind are the shipped ones.
    /// </summary>
    public const string ProviderResultsSection = "ProviderResults";

    /// <summary>
    /// Bounds on what normalization keeps from a result. Recoverable alongside the intake limits it
    /// is tuned together with.
    /// </summary>
    public const string ProviderResultNormalizationSection = "ProviderResultNormalization";

    /// <summary>
    /// The LibreOffice conversion limits. Recoverable because a combination that fails validation
    /// (a temporary-disk allowance below the limits it has to cover) is a mistake an administrator
    /// can only see from the browser that wrote it.
    /// </summary>
    public const string LibreOfficeSection = "LibreOffice";

    /// <summary>Sections a stored value may be dropped from rather than stopping the service.</summary>
    public static IReadOnlyList<string> RecoverableSections { get; } =
    [
        OidcSection,
        AuthenticationSection,
        StorageSection,
        DatabaseSection,
        WorkerSection,
        ProviderResultsSection,
        ProviderResultNormalizationSection,
        LibreOfficeSection,
    ];

    private const int ClaimNameMaximumLength = 255;
    private const int PathMaximumLength = 1024;
    private const long EightGibibytes = 8L * 1024 * 1024 * 1024;

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(
            ParseMaxConcurrency,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "1",
            Minimum: 1,
            Maximum: 64),

        // Worker timing and batch tuning. Duration bounds are whole milliseconds; the floors are the
        // ones ParseRunWorkerOptions.Validate enforces, so a value accepted here is never one the
        // next restart rejects for its own sake. Combinations that only fail together — a heartbeat
        // no shorter than its lease, a poll ceiling below its floor — are caught at startup and
        // dropped with the whole stored section, which is the recovery the recoverable binding
        // exists for.
        new(
            WorkerMaintenanceInterval,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:00:05",
            Minimum: 100),
        new(
            WorkerRecoveryBatchSize,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "100",
            Minimum: 1,
            Maximum: 10_000),
        new(
            WorkerLeaseDuration,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:02:00",
            Minimum: 500),
        new(
            WorkerHeartbeatInterval,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:00:30",
            Minimum: 50),
        new(
            WorkerRetryDelay,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:00:30",
            Minimum: 1),
        new(
            WorkerMinimumPollDelay,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:00:01",
            Minimum: 100),
        new(
            WorkerMaximumPollDelay,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:00:30",
            // No smaller than the floor its own minimum sits on: the poll ceiling can never be
            // shorter than a poll floor that is valid.
            Minimum: 100),
        new(
            WorkerMaxExecutionDuration,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "01:00:00",
            // Zero is the documented "no bound" value, so the floor sits at it; a positive value
            // below one second is refused by validation at startup and recovered there.
            Minimum: 0),
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

        // Local administration sign-in tuning. The session bounds are validation's own: 5 minutes
        // to 7 days is what the options class enforces. The two login keys are narrower than their
        // options class on purpose: both values feed the two ServiceMantle sign-in policies, which
        // accept a permit limit of 1–60 and a window of 10 s–10 min and stop the host at startup
        // outside that range, and a value a browser can store must never be one the next start
        // refuses — the write-time bound and the startup bound are the same bound rather than two
        // that happen to overlap. A deployment pin outside the range still fails at startup as the
        // fail-fast path for whoever has the command line.
        new(
            AuthenticationAdministratorSessionLifetime,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "08:00:00",
            Minimum: 300_000,
            Maximum: 604_800_000),
        new(
            AuthenticationLoginPermitLimit,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "10",
            Minimum: 1,
            Maximum: 60),
        new(
            AuthenticationLoginRateLimitWindow,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:01:00",
            Minimum: 10_000,
            Maximum: 600_000),

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

        // Bounds on the Provider result archive an attempt accepts. The byte caps beyond what
        // validation requires are guards against a misplaced digit rather than claims about what
        // the intake can do: a limit eight billion bytes past its default is not a tuning anyone
        // chose on purpose. Cross-field rules (expansion covering the entry limit) are enforced at
        // startup, where a failing combination drops the stored section.
        new(
            ProviderResultsMaxArchiveBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "536870912",
            Minimum: 1,
            Maximum: EightGibibytes),
        new(
            ProviderResultsMaxEntryCount,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "20000",
            Minimum: 1,
            Maximum: 1_000_000),
        new(
            ProviderResultsMaxEntryBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "268435456",
            Minimum: 1,
            Maximum: EightGibibytes),
        new(
            ProviderResultsMaxExpandedBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "2147483648",
            Minimum: 1,
            Maximum: EightGibibytes),
        new(
            ProviderResultsMaxCompressionRatio,
            SettingKind.Number,
            RequiresRestart: true,
            Default: "200",
            // Validation demands a finite ratio of at least one and caps it nowhere.
            Minimum: 1),
        new(
            ProviderResultsMaxEntryPathBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "2048",
            Minimum: 64,
            Maximum: 16_384),
        new(
            ProviderResultsMaxCentralDirectoryBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "67108864",
            Minimum: 1,
            Maximum: EightGibibytes),

        // Bounds on what normalization keeps. The text limits carry the Int32 ceiling validation
        // imposes, because beyond it the limit is no longer a length anyone can compare against.
        new(
            ProviderResultNormalizationMaxMarkdownBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "67108864",
            Minimum: 1,
            Maximum: int.MaxValue),
        new(
            ProviderResultNormalizationMaxJsonBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "67108864",
            Minimum: 1,
            Maximum: int.MaxValue),
        new(
            ProviderResultNormalizationMaxAssetBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "268435456",
            Minimum: 1,
            Maximum: EightGibibytes),

        // LibreOffice conversion limits. The temporary-disk allowance covering the input and output
        // limits is a cross-field rule validation owns; the floors and the inspection-interval
        // ceiling here are the ones LibreOfficeConversionOptions.Validate enforces per key.
        new(LibreOfficeEnabled, SettingKind.Boolean, RequiresRestart: true, Default: "true"),
        new(
            LibreOfficeMaxConcurrency,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "1",
            Minimum: 1,
            Maximum: 64),
        new(
            LibreOfficeTimeout,
            SettingKind.Duration,
            RequiresRestart: true,
            Default: "00:03:00",
            Minimum: 1000),
        new(
            LibreOfficeResourceInspectionInterval,
            SettingKind.Duration,
            RequiresRestart: true,
            // The invariant round-trippable spelling of 250 milliseconds, so the stored value
            // reports back identically to how it was written.
            Default: "00:00:00.2500000",
            Minimum: 50,
            Maximum: 5000),
        new(
            LibreOfficeMaxInputBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "104857600",
            Minimum: 1,
            Maximum: EightGibibytes),
        new(
            LibreOfficeMaxOutputBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "209715200",
            Minimum: 1,
            Maximum: EightGibibytes),
        new(
            LibreOfficeMaxTemporaryBytes,
            SettingKind.Integer,
            RequiresRestart: true,
            Default: "536870912",
            Minimum: 1,
            Maximum: EightGibibytes),
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

            case SettingKind.Duration:
                if (!TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var duration)
                    || duration < TimeSpan.Zero)
                {
                    return null;
                }

                // Bounds are whole milliseconds because that is the unit every validator behind
                // these keys states its floor in. A maximum of zero is no ceiling, matching the
                // validation that imposes none.
                var milliseconds = duration.TotalMilliseconds;
                if (milliseconds < definition.Minimum
                    || (definition.Maximum > 0 && milliseconds > definition.Maximum))
                {
                    return null;
                }

                // The constant format is invariant and round-trippable, so a value written as
                // "0:30" today is reported as "00:00:30" tomorrow and still binds identically.
                return duration.ToString("c", CultureInfo.InvariantCulture);

            case SettingKind.Number:
                if (!double.TryParse(
                        trimmed,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var ratio)
                    || !double.IsFinite(ratio)
                    || ratio < definition.Minimum
                    || (definition.Maximum > 0 && ratio > definition.Maximum))
                {
                    return null;
                }

                // "R" holds the exact value across a write and the parse that reads it back, so a
                // ratio never drifts by being reported.
                return ratio.ToString("R", CultureInfo.InvariantCulture);

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
