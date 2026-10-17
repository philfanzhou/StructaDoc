using Microsoft.Extensions.Configuration;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Settings;
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Authentication;

/// <summary>
/// Binds the sign-in tuning half of the Authentication section as a recoverable setting. See
/// <see cref="RecoverableConfigurationBinder"/> for why a stored value is dropped rather than fatal.
///
/// The early-bound half — the key ring and the bootstrap credentials — is read from the raw
/// configuration in Program before any of this runs, and an architecture test keeps every one of
/// those keys out of the settings catalog so nothing stored can pretend to reach them.
/// </summary>
public static class AuthenticationTuningConfigurationBinder
{
    public static AuthenticationTuningOptions Bind(
        StructaDocSettingsConfiguration settings,
        SettingsStartupFault fault)
    {
        return RecoverableConfigurationBinder.Bind(
            settings,
            fault,
            SettingCatalog.AuthenticationSection,
            "authentication tuning configuration",
            Read,
            options => options.Validate());
    }

    private static AuthenticationTuningOptions Read(IConfiguration configuration)
    {
        return configuration
            .GetSection(AuthenticationTuningOptions.SectionName)
            .Get<AuthenticationTuningOptions>() ?? new AuthenticationTuningOptions();
    }
}
