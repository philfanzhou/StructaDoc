using System.Collections.Frozen;

namespace StructaDoc.Application.Providers;

/// <summary>
/// One layout-detection input: the bytes of a single page image and its media type.
///
/// The execution contract validates its own shape — a bounded, non-empty image in one of the two
/// supported formats — while the endpoint that calls it owns request validation, authorization,
/// and the caller's deadline (ADR-0013: non-archiving, caller-supplied boundaries). Decoding the
/// image and judging its content is the inference runtime's job; a request that reaches the
/// runtime un-decoded is handed over exactly as the caller supplied it.
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

        Image = image;
        MediaType = normalized;
    }

    public ReadOnlyMemory<byte> Image { get; }

    public string MediaType { get; }
}
