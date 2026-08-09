namespace StructaDoc.Adapters.ControlPlane;

public static class SetupClaim
{
    /// <summary>
    /// A fixed primary key makes the first-run claim a single row the database can arbitrate.
    /// Concurrent claims collide on this key instead of racing an application-level check.
    /// </summary>
    public static readonly Guid SingletonId = new("6c1f5f6e-9d3a-4d0b-9a1e-7f3c2d5b8a40");
}
