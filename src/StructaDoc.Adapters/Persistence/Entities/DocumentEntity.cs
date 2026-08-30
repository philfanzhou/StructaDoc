namespace StructaDoc.Adapters.Persistence.Entities;

public sealed class DocumentEntity
{
    public Guid Id { get; set; }

    public required string OriginalFileName { get; set; }

    public required string MediaType { get; set; }

    public required string Extension { get; set; }

    public long SizeBytes { get; set; }

    public required string Sha256 { get; set; }

    public required string StorageRef { get; set; }

    public byte[]? CreatedByIssuer { get; set; }

    public byte[]? CreatedBySubject { get; set; }

    public byte[]? CreatedByLegacy { get; set; }

    public byte[]? OwnerIssuer { get; set; }

    public byte[]? OwnerSubject { get; set; }

    public string LifecycleState { get; set; } = "active";

    public DateTime? DeletionRequestedAtUtc { get; set; }

    public long ConcurrencyVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<ParseRunEntity> ParseRuns { get; } = [];

    public ICollection<DocumentAccessGrantEntity> AccessGrants { get; } = [];
}
