namespace StructaDoc.Infrastructure.Persistence.Entities;

public sealed class ApiClientEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required byte[] SecretHash { get; set; }
    public required string Scopes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
