namespace StructaDoc.Application.Canonical;

public sealed record ParseBundle(
    string SchemaVersion,
    Guid ParseRunId,
    IReadOnlyList<ParsePage> Pages,
    IReadOnlyList<ParseBlock> Blocks,
    IReadOnlyList<ParseAsset> Assets,
    IReadOnlyList<ParseArtifact> Artifacts,
    string ProviderMetadataJson);

public sealed record ParsePage(
    int Number,
    double? Width = null,
    double? Height = null,
    string? Unit = null,
    string? SourceLocatorJson = null);

public sealed record ParseBlock(
    Guid Id,
    int Sequence,
    int? PageNumber,
    string Type,
    string? Subtype = null,
    string? Content = null,
    string? ContentFormat = null,
    NormalizedBoundingBox? BoundingBox = null,
    double? Confidence = null,
    Guid? AssetId = null,
    string? SourceLocatorJson = null,
    string? ProviderDataJson = null);

public sealed record NormalizedBoundingBox(
    double X0,
    double Y0,
    double X1,
    double Y1);

public sealed record ParseAsset(
    Guid Id,
    string Name,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string StorageRef,
    int? Width = null,
    int? Height = null);

public sealed record ParseArtifact(
    Guid Id,
    string Type,
    string Name,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string StorageRef,
    string? MetadataJson = null);
