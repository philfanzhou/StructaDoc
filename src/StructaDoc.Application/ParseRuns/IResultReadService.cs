using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.ParseRuns;

public interface IResultReadService
{
    Task<IReadOnlyList<RunRecord>> ListForDocumentAsync(
        Guid documentId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<RunRecord?> GetAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PageRecord>?> ListPagesAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<BlockPage?> ListBlocksAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        int limit,
        int? afterSequence = null,
        int? pageNumber = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetRecord>?> ListAssetsAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExportAssetRecord>?> ListAssetsForExportAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactRecord>?> ListArtifactsAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<ResultContent?> OpenAssetAsync(
        Guid parseRunId,
        Guid assetId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<ResultContent?> OpenExportAssetAsync(
        Guid parseRunId,
        ExportAssetRecord asset,
        CancellationToken cancellationToken = default);
    Task<ResultContent?> OpenArtifactAsync(
        Guid parseRunId,
        Guid artifactId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<ResultContent?> OpenMarkdownAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
}

public sealed record PageRecord(
    int Number,
    double? Width,
    double? Height,
    string? Unit,
    string? Label = null,
    string? LabelSource = null);

public sealed record BlockRecord(
    Guid Id,
    int Sequence,
    int? PageNumber,
    string Type,
    string? Subtype,
    string? Content,
    string? ContentFormat,
    BoundingBoxRecord? BoundingBox,
    double? Confidence,
    Guid? AssetId,
    string? Caption = null,
    string? Footnote = null);

public sealed record BoundingBoxRecord(double X0, double Y0, double X1, double Y1);

public sealed record BlockPage(IReadOnlyList<BlockRecord> Items, int? NextSequence);

public sealed record AssetRecord(
    Guid Id,
    string Name,
    string MediaType,
    long SizeBytes,
    string Sha256,
    int? Width,
    int? Height);

/// <summary>
/// Internal export projection. <see cref="StorageRef"/> must never be mapped to a public response.
/// </summary>
public sealed record ExportAssetRecord(
    Guid ParseRunId,
    Guid Id,
    string Name,
    string MediaType,
    long SizeBytes,
    string Sha256,
    int? Width,
    int? Height,
    string StorageRef)
{
    public AssetRecord Metadata =>
        new(Id, Name, MediaType, SizeBytes, Sha256, Width, Height);
}

public sealed record ArtifactRecord(
    Guid Id,
    string Type,
    string Name,
    string MediaType,
    long SizeBytes,
    string Sha256,
    DateTime CreatedAtUtc);

public sealed record ResultContent(Stream Content, string Name, string MediaType, long SizeBytes, string Sha256);
