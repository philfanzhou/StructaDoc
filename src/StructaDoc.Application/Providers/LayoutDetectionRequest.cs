using System.Collections.Frozen;

namespace StructaDoc.Application.Providers;

/// <summary>
/// One layout-detection input: the bytes of a single page image, its media type, and the facts the
/// endpoint's bounded header inspection derived.
///
/// The execution contract validates its own shape — a bounded, non-empty image in one of the two
/// supported formats — while the endpoint that calls it owns request validation, authorization,
/// and the caller's deadline (ADR-0013: non-archiving, caller-supplied boundaries). Decoding the
/// image and judging its content is the inference runtime's job; a request that reaches the
/// runtime un-decoded is handed over exactly as the caller supplied it.
///
/// The optional inspection facts exist for a backend that does not decode the original itself: the
/// cloud backend receives geometry in the orientation-applied frame and reports against the
/// public contract's original-image frame, which needs the header-stored dimensions and the EXIF
/// orientation to map honestly. The runtime backend decodes the image itself and ignores them.
/// </summary>
public sealed record LayoutDetectionRequest
{
    /// <summary>
    /// The request carries one page image in bounded request-scoped memory. The bound is the
    /// contract's own defence-in-depth on top of the endpoint's request limits: it caps what one
    /// execution can allocate for the outbound encoding regardless of who built the request.
    /// </summary>
    public const int MaximumImageBytes = 32 * 1024 * 1024;

    /// <summary>The two media types the layout mode accepts, in the spelling the wire protocol uses.</summary>
    public static FrozenSet<string> SupportedMediaTypes { get; } = new[]
    {
        "image/jpeg",
        "image/png",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public LayoutDetectionRequest(ReadOnlyMemory<byte> image, string mediaType)
        : this(image, mediaType, storedWidth: null, storedHeight: null, exifOrientation: null)
    {
    }

    public LayoutDetectionRequest(
        ReadOnlyMemory<byte> image,
        string mediaType,
        int? storedWidth,
        int? storedHeight,
        int? exifOrientation)
    {
        if (image.IsEmpty)
        {
            throw new ArgumentException("The image is empty.", nameof(image));
        }

        if (image.Length > MaximumImageBytes)
        {
            throw new ArgumentException(
                $"The image exceeds the {MaximumImageBytes}-byte layout detection limit.",
                nameof(image));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        var normalized = mediaType.Trim().ToLowerInvariant();
        if (!SupportedMediaTypes.Contains(normalized))
        {
            throw new ArgumentException(
                "The layout detection media type must be 'image/jpeg' or 'image/png'.",
                nameof(mediaType));
        }

        if (storedWidth.HasValue != storedHeight.HasValue)
        {
            throw new ArgumentException(
                "The stored dimensions must be supplied as a pair.",
                nameof(storedWidth));
        }

        if (storedWidth is <= 0 || storedHeight is <= 0)
        {
            throw new ArgumentException(
                "The stored dimensions must be positive pixel counts.",
                nameof(storedWidth));
        }

        if (exifOrientation is < 1 or > 8)
        {
            throw new ArgumentException(
                "The EXIF orientation must be a value from 1 to 8.",
                nameof(exifOrientation));
        }

        Image = image;
        MediaType = normalized;
        StoredWidth = storedWidth;
        StoredHeight = storedHeight;
        ExifOrientation = exifOrientation;
    }

    public ReadOnlyMemory<byte> Image { get; }

    public string MediaType { get; }

    /// <summary>The header-stored image width in pixels, when the caller's inspection supplied it.</summary>
    public int? StoredWidth { get; }

    /// <summary>The header-stored image height in pixels, when the caller's inspection supplied it.</summary>
    public int? StoredHeight { get; }

    /// <summary>
    /// The EXIF orientation (1–8) the caller's inspection found, when the image carried one. The
    /// values 5–8 are the transposing ones, which swap the stored dimensions when applied.
    /// </summary>
    public int? ExifOrientation { get; }

    /// <summary>
    /// The size the image presents once its EXIF orientation is applied — the frame a backend that
    /// normalizes orientation (the cloud route) works in. Requires the inspection facts; the
    /// runtime backend, which decodes the image itself, never needs them.
    /// </summary>
    public (int OrientedWidth, int OrientedHeight) OrientedDimensions
    {
        get
        {
            var width = StoredWidth!.Value;
            var height = StoredHeight!.Value;
            return ExifOrientation is >= 5 and <= 8 ? (height, width) : (width, height);
        }
    }
}
