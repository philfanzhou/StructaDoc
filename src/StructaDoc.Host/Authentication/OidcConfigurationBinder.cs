using Microsoft.Extensions.Configuration;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Settings;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Authentication;

/// <summary>
/// Binds the identity-provider options as a recoverable section. See
/// <see cref="RecoverableConfigurationBinder"/> for why a stored value is dropped rather than fatal.
///
/// What stays working here is exactly what is needed to repair it: administrators sign in with a
/// local account, which does not depend on the identity provider at all.
/// </summary>
public static class OidcConfigurationBinder
{
    public static OidcAuthenticationOptions Bind(
        StructaDocSettingsConfiguration settings,
        SettingsStartupFault fault)
    {
        return RecoverableConfigurationBinder.Bind(
            settings,
            fault,
            SettingCatalog.OidcSection,
            "identity-provider configuration",
            Read,
            options => options.Validate());
    }

    private static OidcAuthenticationOptions Read(IConfiguration configuration)
    {
        return configuration
            .GetSection(OidcAuthenticationOptions.SectionName)
            .Get<OidcAuthenticationOptions>() ?? new OidcAuthenticationOptions();
    }
}
