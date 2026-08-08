namespace StructaDoc.Infrastructure.ControlPlane.Entities;

/// <summary>
/// One configuration value an administrator set through the web interface. Only keys the service
/// publishes as settable are stored, and only values an administrator actually chose: an absent row
/// means the shipped default applies, which is not the same as a row holding that default.
/// </summary>
public sealed class SettingEntity
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public required string UpdatedBy { get; set; }
}
