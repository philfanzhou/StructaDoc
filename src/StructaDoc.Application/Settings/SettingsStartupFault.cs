namespace StructaDoc.Application.Settings;

/// <summary>
/// A stored setting the service refused while starting, kept so the administration page can say why.
///
/// Settings written from the browser can be wrong in ways configuration files cannot, because there
/// is no operator watching a container log when they are saved. Refusing to start would leave a
/// deployment whose only administration surface is that browser with no way back, so a stored value
/// that fails validation is dropped and recorded here instead, and the service starts without it.
/// A value the deployment pins is not recorded: that operator has a command line and is better served
/// by the service failing immediately.
/// </summary>
public sealed class SettingsStartupFault
{
    /// <summary>The configuration section that was dropped, for example <c>Oidc</c>.</summary>
    public string? Section { get; private set; }

    public string? Detail { get; private set; }

    public bool HasFault => Section is not null;

    public void Record(string section, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        Section = section;
        Detail = detail;
    }
}
