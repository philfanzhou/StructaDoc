using System.Buffers.Binary;

namespace StructaDoc.Host.Layout;

/// <summary>
/// What the endpoint's own bounded look at the submitted bytes concluded.
/// </summary>
internal enum LayoutImageVerdict
{
    /// <summary>A supported image whose oriented dimensions are inside the fixed pixel bounds.</summary>
    Supported,

    /// <summary>Not a structurally usable JPEG or PNG: unsupported, truncated, or malformed.</summary>
    Unsupported,

    /// <summary>A supported image whose oriented dimensions exceed the fixed pixel bounds.</summary>
    PixelBoundExceeded,
}

/// <summary>
/// The bounded, dependency-free image look the layout endpoint performs before handing the bytes
/// to the inference runtime: which of the two supported formats the bytes actually are, the
/// dimensions stored in the header, and the EXIF orientation when one is present.
///
/// This is a signature and header parse, not a decode — decoding is the runtime's job — but it is
/// enough for everything the endpoint must decide itself: the signature, not the client's stated
/// media type, decides the format (the repository's upload-validation rule: never trust a client
/// MIME type alone), and the stored dimensions are bounded so one request cannot describe an
/// unbounded amount of work. The parse is defensive throughout: any malformed structure answers
/// <see cref="LayoutImageVerdict.Unsupported"/>, never an exception.
/// </summary>
/// <param name="Verdict">The conclusion of the look; see <see cref="LayoutImageVerdict"/>.</param>
/// <param name="MediaType">
/// The detected media type (<c>image/jpeg</c> or <c>image/png</c>) when the verdict is Supported,
/// and empty otherwise.
/// </param>
/// <param name="StoredWidth">The header-stored width in pixels when the verdict is Supported.</param>
/// <param name="StoredHeight">The header-stored height in pixels when the verdict is Supported.</param>
/// <param name="ExifOrientation">
/// The EXIF orientation (1–8) when the image carried one. JPEG only: PNG carries no EXIF, which
/// is documented behavior rather than a gap.
/// </param>
internal sealed record LayoutImageInspection(
    LayoutImageVerdict Verdict,
    string MediaType,
    int StoredWidth,
    int StoredHeight,
    int? ExifOrientation)
{
    /// <summary>
    /// The per-dimension bound. No legitimate page image approaches it; it exists so a
    /// syntactically valid header cannot describe a degenerate image.
    /// </summary>
    public const int MaximumDimensionPixels = 32_000;

    /// <summary>
    /// The total-pixel bound across both dimensions, checked against the orientation-adjusted
    /// size the image actually presents.
    /// </summary>
    public const long MaximumTotalPixels = 100_000_000;

    public const string JpegMediaType = "image/jpeg";
    public const string PngMediaType = "image/png";

    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> ExifIdentifier => "Exif"u8;

    public static LayoutImageInspection UnsupportedImage { get; } =
        new(LayoutImageVerdict.Unsupported, string.Empty, 0, 0, null);

    public static LayoutImageInspection PixelBoundExceededImage { get; } =
        new(LayoutImageVerdict.PixelBoundExceeded, string.Empty, 0, 0, null);

    /// <summary>
    /// The size the image presents after its EXIF orientation is applied: orientations 5–8 are
    /// the rotated/transposed ones, which swap the two stored dimensions.
    /// </summary>
    public (int OrientedWidth, int OrientedHeight) OrientedDimensions =>
        ExifOrientation is >= 5 and <= 8
            ? (StoredHeight, StoredWidth)
            : (StoredWidth, StoredHeight);

    public static LayoutImageInspection Parse(ReadOnlySpan<byte> image)
    {
        if (image.Length >= 24 && image.StartsWith(PngSignature))
        {
            return ParsePng(image);
        }

        if (image.Length >= 4 && image[0] == 0xFF && image[1] == 0xD8)
        {
            return ParseJpeg(image);
        }

        return UnsupportedImage;
    }

    /// <summary>
    /// PNG: the signature, then the first chunk must be IHDR carrying the two four-byte
    /// big-endian dimensions. PNG carries no EXIF orientation, so none is reported.
    /// </summary>
    private static LayoutImageInspection ParsePng(ReadOnlySpan<byte> image)
    {
        // The IHDR chunk's declared length is fixed at 13: dimensions (8), bit depth, color type,
        // compression, filter method, and interlace (5).
        const int ihdrLengthOffset = 8;
        const int ihdrTypeOffset = 12;
        const int dimensionOffset = 16;
        const int ihdrDataLength = 13;

        if (BinaryPrimitives.ReadInt32BigEndian(image.Slice(ihdrLengthOffset, 4)) != ihdrDataLength
            || !image.Slice(ihdrTypeOffset, 4).SequenceEqual("IHDR"u8))
        {
            return UnsupportedImage;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(image.Slice(dimensionOffset, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(image.Slice(dimensionOffset + 4, 4));
        if (width <= 0 || height <= 0)
        {
            return UnsupportedImage;
        }

        return Bounded(new LayoutImageInspection(
            LayoutImageVerdict.Supported,
            PngMediaType,
            width,
            height,
            ExifOrientation: null));
    }

    /// <summary>
    /// JPEG: walk the marker segments beneath SOI looking for the first SOF frame header (the
    /// stored dimensions) and any APP1 EXIF orientation, and stop at SOS, where the encoded scan
    /// begins and no further structural information follows. Every segment advances the cursor,
    /// so a bounded buffer cannot loop forever.
    /// </summary>
    private static LayoutImageInspection ParseJpeg(ReadOnlySpan<byte> image)
    {
        int? orientation = null;
        var position = 2;
        while (position + 4 <= image.Length)
        {
            if (image[position] != 0xFF)
            {
                return UnsupportedImage;
            }

            var marker = image[position + 1];
            if (marker == 0xFF)
            {
                // Fill bytes between markers are legal padding; consume one and continue.
                position++;
                continue;
            }

            // Standalone markers carry no length: RST (D0–D7), SOI, EOI. The scan-start marker
            // SOS (DA) is one of them, and it ends the structural region.
            if (marker is 0xD8 or >= 0xD0 and <= 0xD7 or 0xD9)
            {
                position += 2;
                continue;
            }

            if (marker == 0xDA)
            {
                // The frame header never arrived: the stored dimensions are unknown.
                return UnsupportedImage;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(position + 2, 2));
            if (segmentLength < 2)
            {
                // A length below the mandatory two length bytes themselves cannot be stepped over.
                return UnsupportedImage;
            }

            var payloadStart = position + 4;
            var payloadEnd = position + 2 + segmentLength;
            if (payloadEnd > image.Length)
            {
                return UnsupportedImage;
            }

            var payload = image.Slice(payloadStart, payloadEnd - payloadStart);

            // SOF0–SOF15 except DHT (C4), JPG (C8), and DAC (CC): the frame header carrying
            // precision, then height, then width, each big-endian.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (payload.Length < 5)
                {
                    return UnsupportedImage;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2));
                if (width == 0 || height == 0)
                {
                    return UnsupportedImage;
                }

                return Bounded(new LayoutImageInspection(
                    LayoutImageVerdict.Supported,
                    JpegMediaType,
                    width,
                    height,
                    orientation));
            }

            if (marker == 0xE1
                && payload.StartsWith(ExifIdentifier)
                && payload.Length >= 14)
            {
                // "Exif\0\0" precedes the TIFF structure; a malformed one is ignored — the image
                // itself remains usable.
                orientation ??= ReadExifOrientation(payload.Slice(6));
            }

            position = payloadEnd;
        }

        // The structural region ended without a frame header.
        return UnsupportedImage;
    }

    /// <summary>
    /// Reads the orientation tag (0x0112) from the TIFF structure an APP1 EXIF payload carries,
    /// IFD0 only — the directory the JPEG EXIF specification places it in. Every offset is
    /// bounds-checked against the segment, and any malformed value is ignored rather than
    /// rejected: a broken orientation tag does not make the image unusable.
    /// </summary>
    private static int? ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        bool littleEndian;
        if (tiff[0] == 0x49 && tiff[1] == 0x49)
        {
            littleEndian = true;
        }
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D)
        {
            littleEndian = false;
        }
        else
        {
            return null;
        }

        short ReadInt16(ReadOnlySpan<byte> source) => littleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(source)
            : BinaryPrimitives.ReadInt16BigEndian(source);

        int ReadInt32(ReadOnlySpan<byte> source) => littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(source)
            : BinaryPrimitives.ReadInt32BigEndian(source);

        if (ReadInt16(tiff.Slice(2, 2)) != 0x2A)
        {
            return null;
        }

        var ifdOffset = ReadInt32(tiff.Slice(4, 4));
        if (ifdOffset < 8 || ifdOffset + 2 > tiff.Length)
        {
            return null;
        }

        var entryCount = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(ifdOffset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(ifdOffset, 2));

        // One bounded directory: an entry count that would run past the segment is corruption,
        // not a large directory.
        if (entryCount > (tiff.Length - ifdOffset - 2) / 12)
        {
            return null;
        }

        for (var entry = 0; entry < entryCount; entry++)
        {
            var entryOffset = checked(ifdOffset + 2 + (entry * 12));
            if (entryOffset + 12 > tiff.Length)
            {
                return null;
            }

            var field = tiff.Slice(entryOffset, 12);
            if (ReadInt16(field.Slice(0, 2)) != 0x0112)
            {
                continue;
            }

            // Type 3 (SHORT) with one value: the value sits inline in the first two of the four
            // value bytes.
            if (ReadInt16(field.Slice(2, 2)) == 3 && ReadInt32(field.Slice(4, 4)) == 1)
            {
                var value = ReadInt16(field.Slice(8, 2));
                return value is >= 1 and <= 8 ? value : null;
            }

            return null;
        }

        return null;
    }

    private static LayoutImageInspection Bounded(LayoutImageInspection inspection)
    {
        var (orientedWidth, orientedHeight) = inspection.OrientedDimensions;
        return orientedWidth > MaximumDimensionPixels
            || orientedHeight > MaximumDimensionPixels
            || (long)orientedWidth * orientedHeight > MaximumTotalPixels
            ? PixelBoundExceededImage
            : inspection;
    }
}
