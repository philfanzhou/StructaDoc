namespace StructaDoc.Application.Providers;

/// <summary>
/// One detected region of a page image: a generic type label plus its normalized bounding box.
///
/// The box follows the canonical convention of the Canonical Model (Section 9): top-left origin,
/// <c>x</c> increasing rightward, <c>y</c> downward, and coordinates in <c>[0, 1]</c> relative to
/// the source image. The label is the runtime's generic category (for example <c>text</c> or
/// <c>table</c>), never recognized body content — the layout mode detects where things are, not
/// what they say.
/// </summary>
public sealed record LayoutDetectionRegion
{
    public const int MaximumLabelLength = 32;

    public LayoutDetectionRegion(string label, double x0, double y0, double x1, double y1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var normalized = label.Trim().ToLowerInvariant();
        if (normalized.Length > MaximumLabelLength
            || normalized.Any(character => character is not (>= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-')))
        {
            throw new ArgumentException(
                "The region label must be a short generic identifier.",
                nameof(label));
        }

        if (!double.IsFinite(x0) || !double.IsFinite(y0)
            || !double.IsFinite(x1) || !double.IsFinite(y1)
            || x0 < 0 || x0 > x1 || x1 > 1
            || y0 < 0 || y0 > y1 || y1 > 1)
        {
            throw new ArgumentException(
                "The region box must satisfy 0 <= x0 <= x1 <= 1 and 0 <= y0 <= y1 <= 1.",
                nameof(x1));
        }

        Label = normalized;
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
    }

    /// <summary>The generic type label, normalized to lowercase.</summary>
    public string Label { get; }

    public double X0 { get; }

    public double Y0 { get; }

    public double X1 { get; }

    public double Y1 { get; }
}
