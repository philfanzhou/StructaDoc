namespace StructaDoc.Adapters.Persistence.Entities;

public sealed class DocumentAccessGrantEntity
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public DocumentEntity Document { get; set; } = null!;
    public required byte[] PrincipalIssuer { get; set; }
    public required byte[] PrincipalSubject { get; set; }
    public int Permissions { get; set; }
    public byte[]? CreatedByIssuer { get; set; }
    public byte[]? CreatedBySubject { get; set; }
    public byte[]? CreatedByLegacy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
