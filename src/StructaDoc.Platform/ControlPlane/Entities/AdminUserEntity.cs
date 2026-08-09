namespace StructaDoc.Platform.ControlPlane.Entities;

public sealed class AdminUserEntity
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; }
    public Guid SecurityStamp { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}
