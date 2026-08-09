namespace StructaDoc.Application.Settings;

/// <summary>
/// Stored settings the service refused while starting, kept so the administration page can say why.
///
/// Settings written from the browser can be wrong in ways configuration files cannot, because there
/// is no operator watching a container log when they are saved. Refusing to start would leave a
/// deployment whose only administration surface is that browser with no way back, so a stored value
/// that fails validation is dropped and recorded here instead, and the service starts without it.
/// A value the deployment pins is not recorded: that operator has a command line and is better served
/// by the service failing immediately.
///
/// Faults are held per section because more than one can be wrong at a time — a deployment moved to
/// object storage and an external database in the same sitting can get both wrong — and an
/// administrator who is only told about the first would fix it and see the page fail again.
/// </summary>
public sealed class SettingsStartupFault
{
    private readonly Dictionary<string, string> faults = new(StringComparer.Ordinal);

    /// <summary>Each dropped configuration section, for example <c>Oidc</c>, and why.</summary>
    public IReadOnlyDictionary<string, string> Faults => faults;

    public bool HasFault => faults.Count > 0;

    public string? DetailFor(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        return faults.GetValueOrDefault(section);
    }

    public void Record(string section, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        faults[section] = detail;
    }
}
