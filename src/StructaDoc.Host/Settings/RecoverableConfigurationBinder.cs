using Microsoft.Extensions.Configuration;
using StructaDoc.Application.Settings;
using StructaDoc.Adapters.ControlPlane;

namespace StructaDoc.Host.Settings;

/// <summary>
/// Binds a configuration section with the one rule that decides whether a bad value is fatal.
///
/// Invalid configuration normally stops the service, which is the right answer when a person with a
/// command line put it there: failing at once is louder than running wrong. An administrator editing
/// the same values in a browser has no command line, and nobody is watching the container log when
/// they press save. Refusing to start would take away the only surface they could fix it from, so a
/// stored section that fails to bind or validate is dropped and the service starts without it.
///
/// The distinction is the source, not the error. A value the deployment pins still stops the service.
/// </summary>
public static class RecoverableConfigurationBinder
{
    public static TOptions Bind<TOptions>(
        StructaDocSettingsConfiguration settings,
        SettingsStartupFault fault,
        string section,
        string description,
        Func<IConfiguration, TOptions> read,
        Action<TOptions> validate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(validate);

        try
        {
            // Reading is inside the guard because binding fails the same way validation does: a
            // stored value that is not a member of an enum never reaches the validator at all.
            var options = read(settings.Effective);
            validate(options);
            return options;
        }
        catch (InvalidOperationException error) when (settings.IsStoredSection(section))
        {
            fault.Record(
                section,
                $"The stored {description} was rejected while starting and is not in effect: {error.Message}");

            // The whole stored section goes, not just the key that failed. A bucket kept without the
            // credentials it was rejected alongside is not a configuration anyone chose, and half of
            // one is harder to reason about than none.
            var fallback = read(settings.WithoutStoredSection(section));

            // Whatever is left came from the deployment, so this throwing again is the fail-fast
            // path the operator with a command line should get.
            validate(fallback);
            return fallback;
        }
    }
}
