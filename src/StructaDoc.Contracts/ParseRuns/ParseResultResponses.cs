namespace StructaDoc.Contracts.ParseRuns;

public sealed record ParsePageResponse(int Number, double? Width, double? Height, string? Unit);

public sealed record BoundingBoxResponse(double X0, double Y0, double X1, double Y1);

public sealed record ParseBlockResponse(Guid Id, int Sequence, int? PageNumber, string Type, string? Subtype, string? Content, string? ContentFormat, BoundingBoxResponse? BoundingBox, double? Confidence, Guid? AssetId);

public sealed record ParseBlockListResponse(IReadOnlyList<ParseBlockResponse> Items, int? NextSequence);

public sealed record ParseAssetResponse(Guid Id, string Name, string MediaType, long SizeBytes, string Sha256, int? Width, int? Height);

public sealed record ParseArtifactResponse(Guid Id, string Type, string Name, string MediaType, long SizeBytes, string Sha256, DateTime CreatedAt);
