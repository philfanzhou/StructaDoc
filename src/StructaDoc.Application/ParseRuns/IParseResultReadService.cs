using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.ParseRuns;

public interface IParseResultReadService
{
    Task<IReadOnlyList<ParseRunRecord>> ListForDocumentAsync(Guid documentId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<ParseRunRecord?> GetAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParsePageRecord>?> ListPagesAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<ParseBlockPage?> ListBlocksAsync(Guid parseRunId, ResourceAccessContext access, int limit, int? afterSequence = null, int? pageNumber = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParseAssetRecord>?> ListAssetsAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParseArtifactRecord>?> ListArtifactsAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<ParseResultContent?> OpenAssetAsync(Guid parseRunId, Guid assetId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<ParseResultContent?> OpenArtifactAsync(Guid parseRunId, Guid artifactId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<ParseResultContent?> OpenMarkdownAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default);
}

public sealed record ParsePageRecord(int Number, double? Width, double? Height, string? Unit);

public sealed record ParseBlockRecord(Guid Id, int Sequence, int? PageNumber, string Type, string? Subtype, string? Content, string? ContentFormat, BoundingBoxRecord? BoundingBox, double? Confidence, Guid? AssetId);

public sealed record BoundingBoxRecord(double X0, double Y0, double X1, double Y1);

public sealed record ParseBlockPage(IReadOnlyList<ParseBlockRecord> Items, int? NextSequence);

public sealed record ParseAssetRecord(Guid Id, string Name, string MediaType, long SizeBytes, string Sha256, int? Width, int? Height);

public sealed record ParseArtifactRecord(Guid Id, string Type, string Name, string MediaType, long SizeBytes, string Sha256, DateTime CreatedAtUtc);

public sealed record ParseResultContent(Stream Content, string Name, string MediaType, long SizeBytes, string Sha256);
