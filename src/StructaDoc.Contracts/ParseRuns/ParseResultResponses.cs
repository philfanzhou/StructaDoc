namespace StructaDoc.Contracts.ParseRuns;

/// <summary>
/// One page of a Parse Run. <paramref name="Number"/> is the physical one-based page and the only
/// page key Blocks use.
/// </summary>
public sealed record ParsePageResponse(int Number, double? Width, double? Height, string? Unit)
{
    // Added in model 1.1. They are init properties rather than constructor parameters so the API
    // description lists them as optional, as an additive field within /api/v1 must be.

    /// <summary>
    /// The printed page label, such as <c>iv</c> or <c>21</c>, when one is known. It is display and
    /// lookup text that may repeat or be missing, not a key.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Where <see cref="Label"/> came from: <c>pdf-page-labels</c> or <c>page-number-blocks</c>.
    /// Null exactly when <see cref="Label"/> is null.
    /// </summary>
    public string? LabelSource { get; init; }
}

public sealed record BoundingBoxResponse(double X0, double Y0, double X1, double Y1);

public sealed record ParseBlockResponse(Guid Id, int Sequence, int? PageNumber, string Type, string? Subtype, string? Content, string? ContentFormat, BoundingBoxResponse? BoundingBox, double? Confidence, Guid? AssetId)
{
    // Added in model 1.1. They are init properties rather than constructor parameters so the API
    // description lists them as optional, as an additive field within /api/v1 must be.

    /// <summary>
    /// The caption the Provider attached to this figure, table, chart, or code Block, when one was
    /// detected. It is the Provider's inline text (it may contain inline math such as <c>$x$</c>);
    /// treat it as untrusted plain text.
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>
    /// The footnote the Provider attached to this figure, table, chart, or code Block, when one was
    /// detected. Like <see cref="Caption"/>, it is untrusted plain text.
    /// </summary>
    public string? Footnote { get; init; }
}

public sealed record ParseBlockListResponse(IReadOnlyList<ParseBlockResponse> Items, int? NextSequence);

public sealed record ParseAssetResponse(Guid Id, string Name, string MediaType, long SizeBytes, string Sha256, int? Width, int? Height);

public sealed record ParseArtifactResponse(Guid Id, string Type, string Name, string MediaType, long SizeBytes, string Sha256, DateTime CreatedAt);
