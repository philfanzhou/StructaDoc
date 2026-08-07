namespace StructaDoc.Infrastructure.Persistence.Entities;

public sealed class DocumentAccessGrantEntity
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public DocumentEntity Document { get; set; } = null!;
    public required string PrincipalIssuer { get; set; }
    public required string PrincipalSubject { get; set; }
    public int Permissions { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
