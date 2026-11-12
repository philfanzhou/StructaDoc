using System.Collections.Frozen;

namespace StructaDoc.Application.Providers;

/// <summary>
/// The Provider-neutral outcome of one image text-recognition execution (ADR-0015).
///
/// Blocks are in the backend's reading order and use the Canonical Model's Block vocabulary; every
/// box is normalized against <see cref="SourceImageWidth"/> × <see cref="SourceImageHeight"/>, the
/// image's stored frame. A result built from malformed or incomplete backend output is an error,
/// never an empty success, so an empty block list means the backend looked and found nothing.
///
/// Nothing in this result is persisted: the recognition mode creates no Document, Parse Run, Page,
/// Block, Asset, Artifact, or request-result record. All text in it is untrusted backend output.
/// </summary>
public sealed record ImageRecognitionResult
{
    public const int MaximumBlockCount = LayoutDetectionResult.MaximumRegionCount;

    /// <summary>The bound on lines across all blocks of one result.</summary>
    public const int MaximumLineCount = 16384;

    public ImageRecognitionResult(
        IReadOnlyList<ImageRecognitionBlock> blocks,
        int sourceImageWidth,
        int sourceImageHeight,
        string? engine = null,
        string? model = null,
        string? version = null,
        IReadOnlyList<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count > MaximumBlockCount)
        {
            throw new ArgumentOutOfRangeException(nameof(blocks));
        }

        if (blocks.Any(block => block is null))
        {
            throw new ArgumentException("A recognized block cannot be null.", nameof(blocks));
        }

        if (blocks.Sum(block => block.Lines.Count) > MaximumLineCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blocks),
                $"A recognition result carries at most {MaximumLineCount} lines.");
        }

        // The frame, engine, and diagnostics rules are exactly the layout result's; building one
        // validates them in one place rather than restating every bound here.
        var frame = new LayoutDetectionResult(
            [],
            sourceImageWidth,
            sourceImageHeight,
            engine,
            model,
            version,
            diagnostics);

        Blocks = blocks;
        SourceImageWidth = frame.SourceImageWidth;
        SourceImageHeight = frame.SourceImageHeight;
        Engine = frame.Engine;
        Model = frame.Model;
        Version = frame.Version;
        Diagnostics = frame.Diagnostics;
    }

    public IReadOnlyList<ImageRecognitionBlock> Blocks { get; }

    /// <summary>The stored image width in pixels that every box normalizes against.</summary>
    public int SourceImageWidth { get; }

    /// <summary>The stored image height in pixels that every box normalizes against.</summary>
    public int SourceImageHeight { get; }

    public string? Engine { get; }

    public string? Model { get; }

    public string? Version { get; }

    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>
/// One recognized block: a Canonical Model Block type, its primary content in a registered format,
/// its normalized box, and — for text-bearing blocks — its lines in the order the backend reported
/// them, so a caller can keep one printed row together.
/// </summary>
public sealed record ImageRecognitionBlock
{
    public const string PlainFormat = "plain";
    public const string HtmlFormat = "html";
    public const string LatexFormat = "latex";

    /// <summary>The registered Block types of the Canonical Model, Section 8.</summary>
    public static FrozenSet<string> BlockTypes { get; } = new[]
    {
        "title",
        "text",
        "list",
        "table",
        "formula",
        "image",
        "code",
        "header",
        "footer",
        "footnote",
        "page-number",
        "unknown",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The content formats this mode produces.</summary>
    public static FrozenSet<string> ContentFormats { get; } = new[]
    {
        PlainFormat,
        HtmlFormat,
        LatexFormat,
    }.ToFrozenSet(StringComparer.Ordinal);

    public ImageRecognitionBlock(
        string type,
        string? content,
        string? contentFormat,
        ImageRecognitionBox box,
        IReadOnlyList<ImageRecognitionLine> lines,
        string? caption = null,
        string? footnote = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(lines);
        if (!BlockTypes.Contains(type))
        {
            throw new ArgumentException(
                "The block type must be a registered Canonical Model Block type.",
                nameof(type));
        }

        if (content is null != contentFormat is null)
        {
            throw new ArgumentException(
                "A content format is given exactly when content is.",
                nameof(contentFormat));
        }

        if (content is not null && string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Content is null or non-blank text.", nameof(content));
        }

        if (contentFormat is not null && !ContentFormats.Contains(contentFormat))
        {
            throw new ArgumentException(
                "The content format must be 'plain', 'html', or 'latex'.",
                nameof(contentFormat));
        }

        if (lines.Any(line => line is null))
        {
            throw new ArgumentException("A recognized line cannot be null.", nameof(lines));
        }

        if (caption is not null && string.IsNullOrWhiteSpace(caption))
        {
            throw new ArgumentException("A caption is null or non-blank text.", nameof(caption));
        }

        if (footnote is not null && string.IsNullOrWhiteSpace(footnote))
        {
            throw new ArgumentException("A footnote is null or non-blank text.", nameof(footnote));
        }

        Type = type;
        Content = content;
        ContentFormat = contentFormat;
        Box = box;
        Lines = lines;
        Caption = caption;
        Footnote = footnote;
    }

    public string Type { get; }

    public string? Content { get; }

    public string? ContentFormat { get; }

    public ImageRecognitionBox Box { get; }

    public IReadOnlyList<ImageRecognitionLine> Lines { get; }

    /// <summary>Caption text the backend attached to a table, figure, or code block.</summary>
    public string? Caption { get; }

    /// <summary>Footnote text the backend attached to a table, figure, or code block.</summary>
    public string? Footnote { get; }
}

/// <summary>One recognized line of a text-bearing block: its text and its normalized box.</summary>
public sealed record ImageRecognitionLine
{
    public ImageRecognitionLine(string text, ImageRecognitionBox box)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(box);
        Text = text;
        Box = box;
    }

    public string Text { get; }

    public ImageRecognitionBox Box { get; }
}

/// <summary>
/// A normalized box in the Canonical Model's convention (Section 9): top-left origin, <c>x</c>
/// rightward, <c>y</c> downward, and <c>0 &lt;= x0 &lt;= x1 &lt;= 1</c>, <c>0 &lt;= y0 &lt;= y1 &lt;= 1</c>.
/// </summary>
public sealed record ImageRecognitionBox
{
    public ImageRecognitionBox(double x0, double y0, double x1, double y1)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0)
            || !double.IsFinite(x1) || !double.IsFinite(y1)
            || x0 < 0 || x0 > x1 || x1 > 1
            || y0 < 0 || y0 > y1 || y1 > 1)
        {
            throw new ArgumentException(
                "The box must satisfy 0 <= x0 <= x1 <= 1 and 0 <= y0 <= y1 <= 1.",
                nameof(x1));
        }

        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
    }

    public double X0 { get; }

    public double Y0 { get; }

    public double X1 { get; }

    public double Y1 { get; }
}
