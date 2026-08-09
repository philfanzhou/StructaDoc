using Microsoft.Extensions.Configuration;
using StructaDoc.Application.Settings;
using StructaDoc.Infrastructure.Authentication;
using StructaDoc.Infrastructure.ControlPlane;

namespace StructaDoc.Host.Authentication;

/// <summary>
/// Binds the identity-provider options, with the one rule that decides whether a bad value is fatal.
///
/// Invalid configuration normally stops the service, which is the right answer when a person with a
/// command line put it there: failing at once is louder than running wrong. An administrator editing
/// the same values in a browser has no command line, and nobody is watching the container log when
/// they press save. Refusing to start would take away the only surface they could fix it from, so a
/// stored section that fails validation is dropped and the service starts without it. What is left
/// working is exactly what is needed to repair it: administrators sign in with a local account, which
/// does not depend on the identity provider at all.
///
/// The distinction is the source, not the error. A value the deployment pins still stops the service.
/// </summary>
public static class OidcConfigurationBinder
{
    public static OidcAuthenticationOptions Bind(
        StructaDocSettingsConfiguration settings,
        SettingsStartupFault fault)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fault);

        var options = Read(settings.Effective);

        try
        {
            options.Validate();
            return options;
        }
        catch (InvalidOperationException error)
            when (settings.IsStoredSection(SettingCatalog.OidcSection))
        {
            fault.Record(
                SettingCatalog.OidcSection,
                $"The stored identity-provider configuration was rejected while starting and is not in effect: {error.Message}");

            // The whole stored section goes, not just the key that failed. A client id kept without
            // the authority it was rejected alongside is not a configuration anyone chose, and
            // half of one is harder to reason about than none.
            var fallback = Read(settings.WithoutStoredSection(SettingCatalog.OidcSection));

            // Whatever is left came from the deployment, so this throwing again is the fail-fast
            // path the operator with a command line should get.
            fallback.Validate();
            return fallback;
        }
    }

    private static OidcAuthenticationOptions Read(IConfiguration configuration)
    {
        return configuration
            .GetSection(OidcAuthenticationOptions.SectionName)
            .Get<OidcAuthenticationOptions>() ?? new OidcAuthenticationOptions();
    }
}
