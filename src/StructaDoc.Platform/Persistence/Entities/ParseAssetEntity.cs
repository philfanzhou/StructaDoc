namespace StructaDoc.Platform.Persistence.Entities;

public sealed class ParseAssetEntity
{
    public Guid Id { get; set; }

    public Guid ParseRunId { get; set; }

    public ParseRunEntity ParseRun { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string MediaType { get; set; } = null!;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = null!;

    public string StorageRef { get; set; } = null!;

    public int? Width { get; set; }

    public int? Height { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<ParseBlockEntity> Blocks { get; } = [];
}
