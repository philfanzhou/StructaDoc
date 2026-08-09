namespace StructaDoc.Platform.Persistence.Entities;

public sealed class ParseBlockEntity
{
    public Guid Id { get; set; }

    public Guid ParseRunId { get; set; }

    public ParseRunEntity ParseRun { get; set; } = null!;

    public int Sequence { get; set; }

    public int? PageNumber { get; set; }

    public string Type { get; set; } = null!;

    public string? Subtype { get; set; }

    public string? Content { get; set; }

    public string? ContentFormat { get; set; }

    public double? BoundingBoxX0 { get; set; }

    public double? BoundingBoxY0 { get; set; }

    public double? BoundingBoxX1 { get; set; }

    public double? BoundingBoxY1 { get; set; }

    public double? Confidence { get; set; }

    public Guid? AssetId { get; set; }

    public ParseAssetEntity? Asset { get; set; }

    public string? SourceLocatorJson { get; set; }

    public string? ProviderDataJson { get; set; }
}
