using StructaDoc.Contracts.Layout;
using StructaDoc.Contracts.ParseRuns;

namespace StructaDoc.Contracts.Recognition;

/// <summary>
/// The non-archiving image text-recognition answer (ADR-0015): the recognized blocks of one
/// submitted page image, in reading order, produced in the same request, with nothing persisted.
///
/// The frame fields are the layout response's; the blocks use the Canonical Model's Block
/// vocabulary. Every recognized string is untrusted backend output: escape or sanitize it before
/// rendering, and treat table HTML in particular as untrusted markup.
/// </summary>
/// <param name="RequestId">
/// The identifier of this request: the same value the <c>x-correlation-id</c> response header
/// carries. It identifies one exchange only — nothing about the request is stored, so it cannot be
/// used to fetch this answer again.
/// </param>
/// <param name="Width">The source image width in pixels, in the image's stored orientation.</param>
/// <param name="Height">
/// The source image height in pixels, in the image's stored orientation. Together with
/// <paramref name="Width"/> these are the dimensions every box normalizes against.
/// </param>
/// <param name="Blocks">
/// The recognized blocks in the backend's reading order. Reading order is what the backend
/// produces, not a guarantee.
/// </param>
public sealed record ImageRecognizeResponse(
    string RequestId,
    int Width,
    int Height,
    string CoordinateFrame,
    IReadOnlyList<ImageRecognizeBlock> Blocks)
{
    /// <summary>
    /// The one coordinate frame this response ever uses, identical to the layout response's: the
    /// origin is the top-left corner of the reported <c>width</c> × <c>height</c>, <c>x</c>
    /// increases rightward, <c>y</c> downward, and every coordinate is normalized to <c>[0, 1]</c>.
    /// </summary>
    public const string CanonicalCoordinateFrame = LayoutDetectResponse.CanonicalCoordinateFrame;

    /// <summary>
    /// The EXIF orientation value (1–8) the endpoint derived from the submitted image, when the
    /// image carried one. Boxes are in the stored frame; a consumer that displays the original
    /// image applies this value once, the same way it would apply the EXIF tag itself.
    /// </summary>
    public int? Orientation { get; init; }

    /// <summary>Identifies the recognition backend.</summary>
    public string? Engine { get; init; }

    /// <summary>The model the recognition backend used, when it reports one.</summary>
    public string? Model { get; init; }

    /// <summary>The version the recognition backend reports, when it supplies one.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Recoverable notes from the execution, bounded in count and length. Malformed or incompatible
    /// backend output is an error response, never a successful answer that carries diagnostics.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>
/// One recognized block, in the Canonical Model's Block terms (Section 8). Unlike a Parse Run
/// Block it has no resource ID, because the recognition mode persists nothing.
/// </summary>
/// <param name="Sequence">The zero-based, contiguous position of the block in reading order.</param>
/// <param name="Type">
/// A registered Canonical Model Block type, such as <c>text</c>, <c>title</c>, <c>table</c>, or
/// <c>formula</c>. The set may grow within the same API major version; treat an unknown type as
/// displayable or ignorable.
/// </param>
/// <param name="Content">
/// The block's primary content: the lines joined with <c>\n</c> for text-bearing blocks, the
/// recognized table as HTML, or the formula as LaTeX. Null for figures and for blocks with no
/// recognized content.
/// </param>
/// <param name="ContentFormat"><c>plain</c>, <c>html</c>, or <c>latex</c>; null exactly when <paramref name="Content"/> is.</param>
/// <param name="Bbox">The block's normalized box in the response's coordinate frame.</param>
/// <param name="Lines">
/// The block's recognized lines in the order the backend reported them, each with its own box, so
/// a caller can keep one printed row together. Empty for tables, formulas, and figures.
/// </param>
public sealed record ImageRecognizeBlock(
    int Sequence,
    string Type,
    string? Content,
    string? ContentFormat,
    BoundingBoxResponse Bbox,
    IReadOnlyList<ImageRecognizeLine> Lines)
{
    /// <summary>Caption text the backend attached to a table, figure, or code block, lines joined with <c>\n</c>.</summary>
    public string? Caption { get; init; }

    /// <summary>Footnote text the backend attached to a table, figure, or code block, lines joined with <c>\n</c>.</summary>
    public string? Footnote { get; init; }
}

/// <summary>One recognized line: its text and its normalized box.</summary>
public sealed record ImageRecognizeLine(string Text, BoundingBoxResponse Bbox);
