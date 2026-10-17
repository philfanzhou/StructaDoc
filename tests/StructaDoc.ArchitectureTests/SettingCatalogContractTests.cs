using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Conversion;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ProviderResults;
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
        // The key-ring and bootstrap half of Authentication is read from the raw configuration
        // before the store is opened, because the key ring it locates is what decrypts the stored
        // secrets. A settable key in that half would be read too early to have any effect, and
        // would silently do nothing.
        string[] earlyBound =
        [
            "Authentication:DataProtectionKeysPath",
            "Authentication:DataProtectionKeyPersistence",
            "Authentication:DataProtectionRootKey",
            "Authentication:BootstrapAdministratorUsername",
            "Authentication:BootstrapAdministratorPassword",
            "Authentication:BootstrapAdministratorDisplayName",
        ];

        foreach (var key in earlyBound)
        {
            Assert.Null(SettingCatalog.Find(key));
        }

        // The tuning half is the reason the section is split: those keys are bound from the
        // settings-aware configuration, so they are exactly the Authentication keys that may be
        // written from the browser. Locking the list both ways keeps the split from drifting in
        // either direction — a tuning key read early would do nothing, and an early key published
        // as settable would misdescribe what writing it does.
        var settable = SettingCatalog.All
            .Select(definition => definition.Key)
            .Where(key => key.StartsWith(
                StructaDocAuthenticationOptions.SectionName + ":",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            new[]
            {
                SettingCatalog.AuthenticationAdministratorSessionLifetime,
                SettingCatalog.AuthenticationLoginPermitLimit,
                SettingCatalog.AuthenticationLoginRateLimitWindow,
            },
            settable);
    }

    [Fact]
    public void Authentication_tuning_defaults_match_the_options_they_describe()
    {
        var options = new AuthenticationTuningOptions();

        // The parameterless TimeSpan format is the invariant constant format, which is what the
        // catalog stores, so equal here means equal as written rather than equal after parsing.
        Assert.Equal(
            options.AdministratorSessionLifetime.ToString(),
            Definition(SettingCatalog.AuthenticationAdministratorSessionLifetime).Default);
        Assert.Equal(
            options.LoginPermitLimit.ToString(),
            Definition(SettingCatalog.AuthenticationLoginPermitLimit).Default);
        Assert.Equal(
            options.LoginRateLimitWindow.ToString(),
            Definition(SettingCatalog.AuthenticationLoginRateLimitWindow).Default);
    }

    [Fact]
    public void Authentication_tuning_ranges_match_the_bounds_validation_enforces()
    {
        // The session bounds are validation's own, so both edges validate and one step past either
        // fails there.
        var session = Definition(SettingCatalog.AuthenticationAdministratorSessionLifetime);
        TuningWhere(SettingCatalog.AuthenticationAdministratorSessionLifetime, session.Minimum).Validate();
        TuningWhere(SettingCatalog.AuthenticationAdministratorSessionLifetime, session.Maximum).Validate();
        Assert.ThrowsAny<Exception>(
            () => TuningWhere(SettingCatalog.AuthenticationAdministratorSessionLifetime, session.Minimum - 1).Validate());
        Assert.ThrowsAny<Exception>(
            () => TuningWhere(SettingCatalog.AuthenticationAdministratorSessionLifetime, session.Maximum + 1).Validate());

        // The two login keys are narrowed below what their options class accepts, because both
        // values feed the ServiceMantle sign-in policies, which accept a permit limit of 1–60 and a
        // window of 10 s–10 min and stop the host outside that range. The published edges have to
        // stay inside the options class's own bounds — a value accepted here is never one the next
        // restart rejects for its own sake — and stay at or inside the library's, so the exact
        // numbers are held rather than left to drift back toward the wider range.
        var permit = Definition(SettingCatalog.AuthenticationLoginPermitLimit);
        Assert.Equal(1, permit.Minimum);
        Assert.Equal(60, permit.Maximum);
        TuningWhere(SettingCatalog.AuthenticationLoginPermitLimit, permit.Minimum).Validate();
        TuningWhere(SettingCatalog.AuthenticationLoginPermitLimit, permit.Maximum).Validate();
        Assert.ThrowsAny<Exception>(
            () => TuningWhere(SettingCatalog.AuthenticationLoginPermitLimit, permit.Minimum - 1).Validate());
        // The options class still accepts values beyond the published ceiling (up to 1000); the
        // deployment keeps that fail-fast path, and the browser never gets to store one.
        TuningWhere(SettingCatalog.AuthenticationLoginPermitLimit, permit.Maximum + 1).Validate();

        var window = Definition(SettingCatalog.AuthenticationLoginRateLimitWindow);
        Assert.Equal(10_000, window.Minimum);
        Assert.Equal(600_000, window.Maximum);
        TuningWhere(SettingCatalog.AuthenticationLoginRateLimitWindow, window.Minimum).Validate();
        TuningWhere(SettingCatalog.AuthenticationLoginRateLimitWindow, window.Maximum).Validate();
        // The options class's own floor is one second, below the published 10 s; only the ceiling
        // has room left above it (up to one hour), and it is held for the same reason as the
        // permit's.
        TuningWhere(SettingCatalog.AuthenticationLoginRateLimitWindow, 1_000).Validate();
        TuningWhere(SettingCatalog.AuthenticationLoginRateLimitWindow, window.Maximum + 1_000).Validate();
    }

    [Fact]
    public void Keys_are_unique_so_a_lookup_cannot_be_ambiguous()
    {
        var keys = SettingCatalog.All.Select(definition => definition.Key).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Provider_result_intake_defaults_match_the_options_they_describe()
    {
        var options = new ProviderResultIntakeOptions();

        Assert.Equal(
            options.MaxArchiveBytes.ToString(),
            Definition(SettingCatalog.ProviderResultsMaxArchiveBytes).Default);
        Assert.Equal(
            options.MaxEntryCount.ToString(),
            Definition(SettingCatalog.ProviderResultsMaxEntryCount).Default);
        Assert.Equal(
            options.MaxEntryBytes.ToString(),
            Definition(SettingCatalog.ProviderResultsMaxEntryBytes).Default);
        Assert.Equal(
            options.MaxExpandedBytes.ToString(),
            Definition(SettingCatalog.ProviderResultsMaxExpandedBytes).Default);
        Assert.Equal(
            options.MaxCompressionRatio.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Definition(SettingCatalog.ProviderResultsMaxCompressionRatio).Default);
        Assert.Equal(
            options.MaxEntryPathBytes.ToString(),
            Definition(SettingCatalog.ProviderResultsMaxEntryPathBytes).Default);
        Assert.Equal(
            options.MaxCentralDirectoryBytes.ToString(),
            Definition(SettingCatalog.ProviderResultsMaxCentralDirectoryBytes).Default);
    }

    [Fact]
    public void Provider_result_normalization_defaults_match_the_options_they_describe()
    {
        var options = new ProviderResultNormalizationOptions();

        Assert.Equal(
            options.MaxMarkdownBytes.ToString(),
            Definition(SettingCatalog.ProviderResultNormalizationMaxMarkdownBytes).Default);
        Assert.Equal(
            options.MaxJsonBytes.ToString(),
            Definition(SettingCatalog.ProviderResultNormalizationMaxJsonBytes).Default);
        Assert.Equal(
            options.MaxAssetBytes.ToString(),
            Definition(SettingCatalog.ProviderResultNormalizationMaxAssetBytes).Default);
    }

    [Fact]
    public void Document_conversion_defaults_match_the_options_they_describe()
    {
        var options = new LibreOfficeConversionOptions();

        Assert.Equal(
            options.Enabled ? "true" : "false",
            Definition(SettingCatalog.LibreOfficeEnabled).Default);
        Assert.Equal(
            options.MaxConcurrency.ToString(),
            Definition(SettingCatalog.LibreOfficeMaxConcurrency).Default);
        // The parameterless TimeSpan format is the invariant constant format, which is what the
        // catalog stores, so equal here means equal as written rather than equal after parsing.
        Assert.Equal(
            options.Timeout.ToString(),
            Definition(SettingCatalog.LibreOfficeTimeout).Default);
        Assert.Equal(
            options.ResourceInspectionInterval.ToString(),
            Definition(SettingCatalog.LibreOfficeResourceInspectionInterval).Default);
        Assert.Equal(
            options.MaxInputBytes.ToString(),
            Definition(SettingCatalog.LibreOfficeMaxInputBytes).Default);
        Assert.Equal(
            options.MaxOutputBytes.ToString(),
            Definition(SettingCatalog.LibreOfficeMaxOutputBytes).Default);
        Assert.Equal(
            options.MaxTemporaryBytes.ToString(),
            Definition(SettingCatalog.LibreOfficeMaxTemporaryBytes).Default);
    }

    /// <summary>
    /// A range the catalog publishes has to be one the options behind it accept, or an
    /// administrator can save a value the next restart rejects for its own sake. The edges are
    /// what prove it: the minimum and maximum themselves must validate, and where validation
    /// states its own floor, one step below it must fail there, so the write-time bound and the
    /// startup bound are the same bound rather than two that happen to overlap.
    /// </summary>
    [Fact]
    public void Provider_result_intake_ranges_match_the_bounds_validation_enforces()
    {
        foreach (var key in new[]
        {
            SettingCatalog.ProviderResultsMaxArchiveBytes,
            SettingCatalog.ProviderResultsMaxEntryCount,
            SettingCatalog.ProviderResultsMaxEntryBytes,
            SettingCatalog.ProviderResultsMaxCentralDirectoryBytes,
        })
        {
            var definition = Definition(key);
            IntakeWhere(key, definition.Minimum).Validate();
            Assert.ThrowsAny<Exception>(() => IntakeWhere(key, definition.Minimum - 1).Validate());
        }

        // The expansion limit is floored not only by positivity but by having to cover the entry
        // limit, so its floor probe lowers the entry limit with it rather than leaving the default
        // beside it.
        var expanded = Definition(SettingCatalog.ProviderResultsMaxExpandedBytes);
        new ProviderResultIntakeOptions
        {
            MaxExpandedBytes = expanded.Minimum,
            MaxEntryBytes = expanded.Minimum,
        }.Validate();
        Assert.ThrowsAny<Exception>(() => new ProviderResultIntakeOptions
        {
            MaxExpandedBytes = expanded.Minimum - 1,
            MaxEntryBytes = expanded.Minimum,
        }.Validate());

        // The entry-path limit is the one that carries both of validation's own bounds.
        var entryPath = Definition(SettingCatalog.ProviderResultsMaxEntryPathBytes);
        IntakeWhere(SettingCatalog.ProviderResultsMaxEntryPathBytes, entryPath.Minimum).Validate();
        IntakeWhere(SettingCatalog.ProviderResultsMaxEntryPathBytes, entryPath.Maximum).Validate();
        Assert.ThrowsAny<Exception>(
            () => IntakeWhere(SettingCatalog.ProviderResultsMaxEntryPathBytes, entryPath.Minimum - 1).Validate());
        Assert.ThrowsAny<Exception>(
            () => IntakeWhere(SettingCatalog.ProviderResultsMaxEntryPathBytes, entryPath.Maximum + 1).Validate());

        // The compression ratio is a number rather than an integer. Validation demands a finite
        // value of at least one and caps it nowhere, which the catalog restates.
        var ratio = Definition(SettingCatalog.ProviderResultsMaxCompressionRatio);
        Assert.Equal(SettingKind.Number, ratio.Kind);
        Assert.Equal(1, ratio.Minimum);
        Assert.Equal(0, ratio.Maximum);
        new ProviderResultIntakeOptions { MaxCompressionRatio = ratio.Minimum }.Validate();
        Assert.ThrowsAny<Exception>(
            () => new ProviderResultIntakeOptions { MaxCompressionRatio = ratio.Minimum - 0.5 }.Validate());
    }

    [Fact]
    public void Provider_result_normalization_ranges_match_the_bounds_validation_enforces()
    {
        foreach (var key in new[]
        {
            SettingCatalog.ProviderResultNormalizationMaxMarkdownBytes,
            SettingCatalog.ProviderResultNormalizationMaxJsonBytes,
            SettingCatalog.ProviderResultNormalizationMaxAssetBytes,
        })
        {
            var definition = Definition(key);
            NormalizationWhere(key, definition.Minimum).Validate();
            NormalizationWhere(key, definition.Maximum).Validate();
            Assert.ThrowsAny<Exception>(
                () => NormalizationWhere(key, definition.Minimum - 1).Validate());
        }

        // The text limits carry the Int32 ceiling validation imposes, rather than a guard this
        // catalog invented: a length nobody can compare against is not a length.
        Assert.Equal(int.MaxValue, Definition(SettingCatalog.ProviderResultNormalizationMaxMarkdownBytes).Maximum);
        Assert.Equal(int.MaxValue, Definition(SettingCatalog.ProviderResultNormalizationMaxJsonBytes).Maximum);
    }

    [Fact]
    public void Conversion_ranges_match_the_bounds_validation_enforces()
    {
        foreach (var key in new[]
        {
            SettingCatalog.LibreOfficeMaxConcurrency,
            SettingCatalog.LibreOfficeMaxInputBytes,
            SettingCatalog.LibreOfficeMaxOutputBytes,
        })
        {
            var definition = Definition(key);
            ConversionWhere(key, definition.Minimum).Validate();
            Assert.ThrowsAny<Exception>(() => ConversionWhere(key, definition.Minimum - 1).Validate());
        }

        // The temporary-disk allowance is floored not only by positivity but by having to cover
        // the input and output limits, so its floor probe lowers those with it.
        var temporary = Definition(SettingCatalog.LibreOfficeMaxTemporaryBytes);
        new LibreOfficeConversionOptions
        {
            MaxInputBytes = temporary.Minimum,
            MaxOutputBytes = temporary.Minimum,
            MaxTemporaryBytes = temporary.Minimum,
        }.Validate();
        Assert.ThrowsAny<Exception>(() => new LibreOfficeConversionOptions
        {
            MaxInputBytes = temporary.Minimum,
            MaxOutputBytes = temporary.Minimum,
            MaxTemporaryBytes = temporary.Minimum - 1,
        }.Validate());

        var timeout = Definition(SettingCatalog.LibreOfficeTimeout);
        Assert.Equal(SettingKind.Duration, timeout.Kind);
        ConversionWhere(SettingCatalog.LibreOfficeTimeout, timeout.Minimum).Validate();
        Assert.ThrowsAny<Exception>(
            () => ConversionWhere(SettingCatalog.LibreOfficeTimeout, timeout.Minimum - 1).Validate());

        // The inspection interval is the one conversion limit validation caps as well as floors.
        var inspection = Definition(SettingCatalog.LibreOfficeResourceInspectionInterval);
        ConversionWhere(
            SettingCatalog.LibreOfficeResourceInspectionInterval,
            inspection.Minimum).Validate();
        ConversionWhere(
            SettingCatalog.LibreOfficeResourceInspectionInterval,
            inspection.Maximum).Validate();
        Assert.ThrowsAny<Exception>(
            () => ConversionWhere(
                SettingCatalog.LibreOfficeResourceInspectionInterval,
                inspection.Minimum - 1).Validate());
        Assert.ThrowsAny<Exception>(
            () => ConversionWhere(
                SettingCatalog.LibreOfficeResourceInspectionInterval,
                inspection.Maximum + 1).Validate());
    }

    /// <summary>
    /// Paths inside the running image and the Worker on/off switch are deployment facts, not
    /// tuning: a browser cannot see the filesystem it would be pointing at, and a Worker switch
    /// reachable from the page is the silent-queue failure mode this catalog already removed once.
    /// </summary>
    [Fact]
    public void Deployment_owned_keys_stay_out_of_the_catalog()
    {
        string[] deploymentOwned =
        [
            "Worker:Enabled",
            "ProviderResults:TemporaryPath",
            "ProviderResultNormalization:TemporaryPath",
            "LibreOffice:TemporaryPath",
            "LibreOffice:ExecutablePath",
        ];

        foreach (var key in deploymentOwned)
        {
            Assert.Null(SettingCatalog.Find(key));
        }
    }

    /// <summary>
    /// One field set to the value under test, every other field left at the default the options
    /// class ships, because a range probe that quietly changed two fields would prove nothing
    /// about either.
    /// </summary>
    private static ProviderResultIntakeOptions IntakeWhere(string key, long value)
    {
        var defaults = new ProviderResultIntakeOptions();
        return new ProviderResultIntakeOptions
        {
            MaxArchiveBytes = Pick(key, SettingCatalog.ProviderResultsMaxArchiveBytes, value, defaults.MaxArchiveBytes),
            MaxEntryCount = (int)Pick(key, SettingCatalog.ProviderResultsMaxEntryCount, value, defaults.MaxEntryCount),
            MaxEntryBytes = Pick(key, SettingCatalog.ProviderResultsMaxEntryBytes, value, defaults.MaxEntryBytes),
            MaxExpandedBytes = Pick(key, SettingCatalog.ProviderResultsMaxExpandedBytes, value, defaults.MaxExpandedBytes),
            MaxEntryPathBytes = (int)Pick(key, SettingCatalog.ProviderResultsMaxEntryPathBytes, value, defaults.MaxEntryPathBytes),
            MaxCentralDirectoryBytes = Pick(
                key,
                SettingCatalog.ProviderResultsMaxCentralDirectoryBytes,
                value,
                defaults.MaxCentralDirectoryBytes),
        };
    }

    private static ProviderResultNormalizationOptions NormalizationWhere(string key, long value)
    {
        var defaults = new ProviderResultNormalizationOptions();
        return new ProviderResultNormalizationOptions
        {
            MaxMarkdownBytes = Pick(
                key,
                SettingCatalog.ProviderResultNormalizationMaxMarkdownBytes,
                value,
                defaults.MaxMarkdownBytes),
            MaxJsonBytes = Pick(
                key,
                SettingCatalog.ProviderResultNormalizationMaxJsonBytes,
                value,
                defaults.MaxJsonBytes),
            MaxAssetBytes = Pick(
                key,
                SettingCatalog.ProviderResultNormalizationMaxAssetBytes,
                value,
                defaults.MaxAssetBytes),
        };
    }

    private static LibreOfficeConversionOptions ConversionWhere(string key, long milliseconds)
    {
        var defaults = new LibreOfficeConversionOptions();
        return new LibreOfficeConversionOptions
        {
            MaxConcurrency = (int)Pick(key, SettingCatalog.LibreOfficeMaxConcurrency, milliseconds, defaults.MaxConcurrency),
            Timeout = TimeSpan.FromMilliseconds(
                Pick(key, SettingCatalog.LibreOfficeTimeout, milliseconds, defaults.Timeout.TotalMilliseconds)),
            ResourceInspectionInterval = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.LibreOfficeResourceInspectionInterval,
                milliseconds,
                defaults.ResourceInspectionInterval.TotalMilliseconds)),
            MaxInputBytes = Pick(key, SettingCatalog.LibreOfficeMaxInputBytes, milliseconds, defaults.MaxInputBytes),
            MaxOutputBytes = Pick(key, SettingCatalog.LibreOfficeMaxOutputBytes, milliseconds, defaults.MaxOutputBytes),
            MaxTemporaryBytes = Pick(
                key,
                SettingCatalog.LibreOfficeMaxTemporaryBytes,
                milliseconds,
                defaults.MaxTemporaryBytes),
        };
    }

    private static AuthenticationTuningOptions TuningWhere(string key, long milliseconds)
    {
        var defaults = new AuthenticationTuningOptions();
        return new AuthenticationTuningOptions
        {
            AdministratorSessionLifetime = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.AuthenticationAdministratorSessionLifetime,
                milliseconds,
                defaults.AdministratorSessionLifetime.TotalMilliseconds)),
            LoginPermitLimit = (int)Pick(
                key,
                SettingCatalog.AuthenticationLoginPermitLimit,
                milliseconds,
                defaults.LoginPermitLimit),
            LoginRateLimitWindow = TimeSpan.FromMilliseconds(Pick(
                key,
                SettingCatalog.AuthenticationLoginRateLimitWindow,
                milliseconds,
                defaults.LoginRateLimitWindow.TotalMilliseconds)),
        };
    }

    private static long Pick(string key, string candidate, long value, long fallback) =>
        key == candidate ? value : fallback;

    private static double Pick(string key, string candidate, long value, double fallback) =>
        key == candidate ? value : fallback;

    private static SettingDefinition Definition(string key)
    {
        return SettingCatalog.Find(key)
            ?? throw new InvalidOperationException($"'{key}' is missing from the catalog.");
    }
}
