namespace StructaDoc.Application.Canonical;

/// <summary>
/// Reads the <c>/PageLabels</c> number tree of a PDF. Only that tree is read, and its traversal is
/// bounded.
/// </summary>
public interface IPdfPageLabelReader
{
    /// <summary>
    /// Opens the PDF through <paramref name="openReadAsync"/> and reads its page-label ranges.
    /// A PDF that cannot be parsed, or whose tree is malformed, is a result rather than an
    /// exception. Exceptions from opening or reading the stream itself propagate unchanged, and so
    /// does cancellation.
    /// </summary>
    Task<PdfPageLabelReadResult> ReadAsync(
        Func<CancellationToken, Task<Stream>> openReadAsync,
        CancellationToken cancellationToken = default);
}

public enum PdfPageLabelReadStatus
{
    /// <summary>The PDF has no <c>/PageLabels</c> entry.</summary>
    Absent,

    /// <summary>The PDF could not be parsed: corrupt, encrypted, or unsupported.</summary>
    Unreadable,

    /// <summary>The tree has a wrong type, decreasing keys, an unknown style, a cycle, or exceeds a limit.</summary>
    Malformed,

    /// <summary>The tree was read completely.</summary>
    Valid,
}

public enum PdfPageLabelStyle
{
    /// <summary>No <c>/S</c> entry: the label is the prefix alone.</summary>
    None,

    /// <summary><c>/D</c>: decimal arabic numerals.</summary>
    Decimal,

    /// <summary><c>/R</c>: uppercase roman numerals.</summary>
    UpperRoman,

    /// <summary><c>/r</c>: lowercase roman numerals.</summary>
    LowerRoman,

    /// <summary><c>/A</c>: uppercase letters, A to Z, then AA to ZZ, and so on.</summary>
    UpperLetters,

    /// <summary><c>/a</c>: lowercase letters.</summary>
    LowerLetters,
}

/// <summary>
/// One page-label range: it starts at the zero-based physical page index
/// <paramref name="StartPageIndex"/> and lasts until the next range starts.
/// </summary>
public sealed record PdfPageLabelRange(
    long StartPageIndex,
    PdfPageLabelStyle Style,
    string? Prefix,
    long StartValue);

public sealed record PdfPageLabelReadResult(
    PdfPageLabelReadStatus Status,
    IReadOnlyList<PdfPageLabelRange> Ranges)
{
    public static PdfPageLabelReadResult Absent { get; } = new(PdfPageLabelReadStatus.Absent, []);

    public static PdfPageLabelReadResult Unreadable { get; } = new(PdfPageLabelReadStatus.Unreadable, []);

    public static PdfPageLabelReadResult Malformed { get; } = new(PdfPageLabelReadStatus.Malformed, []);
}
