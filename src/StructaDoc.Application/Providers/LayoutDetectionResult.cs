namespace StructaDoc.Application.Providers;

/// <summary>
/// The Provider-neutral outcome of one layout-detection execution.
///
/// Regions are normalized boxes in the canonical convention; the source image dimensions are the
/// ones the runtime reports from decoding the image; engine, model, and version identify the
/// runtime when it supplies them. Diagnostics is the bounded slot for irregular output an
/// execution still answered: a result built from malformed or incomplete runtime output is an
/// error, never an empty success, so a successful result's diagnostics describe recoverable
/// notes only and is empty unless the runtime had something to say.
///
/// Nothing in this result is persisted: the layout mode creates no Document, Parse Run, Page,
/// Block, Asset, Artifact, or request-result record (ADR-0013).
/// </summary>
public sealed record LayoutDetectionResult
{
    public const int MaximumRegionCount = 4096;
    public const int MaximumEngineInformationLength = 128;
    public const int MaximumDiagnosticCount = 16;
    public const int MaximumDiagnosticLength = 256;

    public LayoutDetectionResult(
        IReadOnlyList<LayoutDetectionRegion> regions,
        int sourceImageWidth,
        int sourceImageHeight,
        string? engine = null,
        string? model = null,
        string? version = null,
        IReadOnlyList<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count > MaximumRegionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(regions));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceImageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceImageHeight);

        var boundedDiagnostics = diagnostics?.ToArray() ?? [];
        if (boundedDiagnostics.Length > MaximumDiagnosticCount
            || boundedDiagnostics.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Diagnostics must be a short list of non-empty notes.",
                nameof(diagnostics));
        }

        Regions = regions;
        SourceImageWidth = sourceImageWidth;
        SourceImageHeight = sourceImageHeight;
        Engine = ValidateEngineInformation(engine);
        Model = ValidateEngineInformation(model);
        Version = ValidateEngineInformation(version);
        Diagnostics = boundedDiagnostics;
    }

    public IReadOnlyList<LayoutDetectionRegion> Regions { get; }

    /// <summary>The source image width in pixels as the runtime decoded it.</summary>
    public int SourceImageWidth { get; }

    /// <summary>The source image height in pixels as the runtime decoded it.</summary>
    public int SourceImageHeight { get; }

    public string? Engine { get; }

    public string? Model { get; }

    public string? Version { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    private static string? ValidateEngineInformation(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var sanitized = new string(
            value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (sanitized.Length == 0 || sanitized.Length > MaximumEngineInformationLength)
        {
            throw new ArgumentException(
                "Engine information must be a short non-empty string.",
                nameof(value));
        }

        return sanitized;
    }
}
