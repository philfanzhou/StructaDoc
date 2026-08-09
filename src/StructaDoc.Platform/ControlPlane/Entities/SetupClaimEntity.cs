namespace StructaDoc.Platform.ControlPlane.Entities;

/// <summary>
/// Records who claimed the first administrator account. The service cannot tell an operator from
/// any other reachable client during first run, so the claim is made attributable after the fact
/// instead of being silently trusted.
/// </summary>
public sealed class SetupClaimEntity
{
    public Guid Id { get; set; }
    public Guid AdministratorId { get; set; }
    public required string ClaimedFromAddress { get; set; }
    public DateTime ClaimedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
}
