namespace StructaDoc.Platform.ControlPlane;

public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// Always a local SQLite file. There is deliberately no provider switch: the control plane must
    /// work before anything has been configured, so it cannot depend on a configured database.
    /// </summary>
    public string DatabasePath { get; init; } = "./data/control.db";

    public string ConnectionString => $"Data Source={DatabasePath}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("ControlPlane:DatabasePath must be configured.");
        }
    }
}
