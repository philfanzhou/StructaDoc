namespace StructaDoc.Contracts.Layout;

/// <summary>
/// The non-archiving layout-detection answer (ADR-0013): normalized regions for one submitted
/// page image, produced in the same request, with nothing persisted on either side.
///
/// The mode detects where content sits, never what it says: no recognized text, table HTML,
/// formula LaTeX, cropped assets, Markdown, or download URLs appear anywhere in this response.
/// </summary>
/// <param name="RequestId">
/// The identifier of this request: the same value the <c>x-correlation-id</c> response header
/// carries, so an accepted result can be quoted beside a service log line. It identifies one
/// exchange only — nothing about the request is stored, so it cannot be used to fetch this
/// answer again.
/// </param>
/// <param name="Width">The source image width in pixels, as the inference runtime decoded it.</param>
/// <param name="Height">
/// The source image height in pixels, as the inference runtime decoded it. Together with
/// <paramref name="Width"/> these are the dimensions every region coordinate normalizes against.
/// </param>
/// <param name="Regions">
/// The detected regions in detection sequence. The sequence is a detection pass over the page,
/// not a guaranteed reading order.
/// </param>
public sealed record LayoutDetectResponse(
    string RequestId,
    int Width,
    int Height,
    string CoordinateFrame,
    IReadOnlyList<LayoutDetectRegion> Regions)
{
    /// <summary>
    /// The one coordinate frame this response ever uses, stated in the body so a consumer cannot
    /// mistake it: the origin is the top-left corner of the reported <c>width</c> × <c>height</c>,
    /// <c>x</c> increases rightward, <c>y</c> increases downward, and every region coordinate is
    /// normalized to <c>[0, 1]</c> against those dimensions — the canonical convention of the
    /// Canonical Model, Section 9.
    /// </summary>
    public const string CanonicalCoordinateFrame = "top-left-origin-normalized";

    /// <summary>
    /// The EXIF orientation value (1–8) the endpoint derived from the submitted image, when the
    /// image carried one. Null means no orientation tag was present, so the stored pixels are
    /// already upright. The reported dimensions are the ones the runtime decoded, in the image's
    /// stored orientation; a consumer that displays the original image should apply this value
    /// the same way it would apply the EXIF tag itself.
    /// </summary>
    public int? Orientation { get; init; }

    /// <summary>Identifies the inference runtime, when it reports itself.</summary>
    public string? Engine { get; init; }

    /// <summary>The model name the inference runtime reports, when it supplies one.</summary>
    public string? Model { get; init; }

    /// <summary>The version the inference runtime reports, when it supplies one.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Recoverable notes from the execution, bounded in count and length. Empty unless the
    /// runtime had something to say; malformed or unsupported runtime output is an error
    /// response, never a successful answer that carries diagnostics instead.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>
/// One detected region of the submitted image: a generic type label plus its normalized bounding
/// box in <see cref="LayoutDetectResponse.CanonicalCoordinateFrame"/>.
/// </summary>
/// <param name="Id">
/// This response's own region number: the array index. It is a local reference for reading one
/// reply, not a Canonical Model resource ID — no resource exists to identify, because the layout
/// mode persists nothing.
/// </param>
/// <param name="Label">
/// The generic category the runtime detected, such as <c>text</c> or <c>table</c> — never
/// recognized body content.
/// </param>
public sealed record LayoutDetectRegion(
    int Id,
    string Label,
    double X0,
    double Y0,
    double X1,
    double Y1);
