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

    public string? CreatedBy { get; set; }

    public string? OwnerIssuer { get; set; }

    public string? OwnerSubject { get; set; }

    public string LifecycleState { get; set; } = "active";

    public DateTime? DeletionRequestedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? MetadataJson { get; set; }

    public ICollection<ParseRunEntity> ParseRuns { get; } = [];

    public ICollection<DocumentAccessGrantEntity> AccessGrants { get; } = [];
}
