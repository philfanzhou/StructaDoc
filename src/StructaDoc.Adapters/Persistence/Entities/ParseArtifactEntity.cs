namespace StructaDoc.Adapters.Persistence.Entities;

public sealed class ParseArtifactEntity
{
    public Guid Id { get; set; }

    public Guid ParseRunId { get; set; }

    public ParseRunEntity ParseRun { get; set; } = null!;

    public string Type { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string MediaType { get; set; } = null!;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = null!;

    public string StorageRef { get; set; } = null!;

    public string? MetadataJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
