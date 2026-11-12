using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using StructaDoc.Application.Providers;

namespace StructaDoc.Host.Layout;

/// <summary>
/// The single-image intake both non-archiving endpoints share — layout detection (ADR-0013) and
/// image text recognition (ADR-0015) take the same input under the same rules: one JPEG or PNG in
/// the multipart <c>file</c> field, read into bounded memory (never spilled to a temporary file),
/// with the format decided by the file signature, the byte and pixel bounds enforced, and the EXIF
/// orientation derived.
///
/// A refusal is returned as an <see cref="ImageIntakeRejection"/> whose <c>Kind</c> each endpoint
/// prefixes with its own problem-code family, so the two routes report the same failure under
/// their own stable codes.
/// </summary>
internal static class NonArchivingImageIntake
{
    /// <summary>The multipart overhead allowance, in the same shape as the document upload bound.</summary>
    public const long MultipartOverheadAllowance = 1024 * 1024;

    public const int MaximumImageBytes = LayoutDetectionRequest.MaximumImageBytes;

    public const string RequestInvalid = "request-invalid";

    public const string MediaTypeUnsupported = "media-type-unsupported";

    public const string InputTooLarge = "input-too-large";

    private const int MaximumSectionHeaderCount = 16;

    private const int MaximumSectionHeaderLength = 4096;

    /// <summary>
    /// Decides everything about the request shape before the body is read or a slot is taken:
    /// content type, then the multipart boundary.
    /// </summary>
    public static ImageIntakeRejection? CheckShape(HttpRequest request, out string boundary)
    {
        boundary = string.Empty;
        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var requestMediaType)
            || requestMediaType!.MediaType is not { } requestMediaTypeName
            || !requestMediaTypeName.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return new ImageIntakeRejection(
                StatusCodes.Status400BadRequest,
                RequestInvalid,
                "The request must use multipart/form-data.");
        }

        boundary = request.GetMultipartBoundary();
        if (string.IsNullOrEmpty(boundary))
        {
            return new ImageIntakeRejection(
                StatusCodes.Status400BadRequest,
                RequestInvalid,
                "The multipart request carries no boundary.");
        }

        return null;
    }

    /// <summary>
    /// Reads and inspects the one image. <paramref name="modeName"/> names the mode in the byte
    /// bound's message, such as <c>layout detection</c>.
    /// </summary>
    public static async Task<ImageIntake> ReadAsync(
        HttpRequest request,
        string boundary,
        string modeName,
        CancellationToken cancellationToken)
    {
        var image = await ReadSingleImageFileAsync(request, boundary, modeName, cancellationToken);
        if (image.Rejection is not null)
        {
            return new ImageIntake(null, null, image.Rejection);
        }

        var inspection = LayoutImageInspection.Parse(image.Bytes.Span);
        if (inspection.Verdict == LayoutImageVerdict.Unsupported)
        {
            return Rejected(
                StatusCodes.Status415UnsupportedMediaType,
                MediaTypeUnsupported,
                "The submitted image must be a JPEG or a PNG.");
        }

        if (inspection.Verdict == LayoutImageVerdict.PixelBoundExceeded)
        {
            return Rejected(
                StatusCodes.Status413PayloadTooLarge,
                InputTooLarge,
                $"The image exceeds the pixel bounds of {LayoutImageInspection.MaximumDimensionPixels} "
                    + $"per dimension and {LayoutImageInspection.MaximumTotalPixels} in total.");
        }

        // The signature has decided the format. A declared content type that contradicts it — or
        // names an unsupported format at all — is refused rather than trusted.
        if (image.DeclaredMediaType is { } declared
            && (!MediaTypeHeaderValue.TryParse(declared, out var declaredMediaType)
                || declaredMediaType!.MediaType is not { } declaredName
                || !declaredName.Equals(inspection.MediaType, StringComparison.OrdinalIgnoreCase)))
        {
            return Rejected(
                StatusCodes.Status415UnsupportedMediaType,
                MediaTypeUnsupported,
                $"The declared content type does not match the submitted {inspection.MediaType} image.");
        }

        return new ImageIntake(
            new LayoutDetectionRequest(
                image.Bytes,
                inspection.MediaType,
                inspection.StoredWidth,
                inspection.StoredHeight,
                inspection.ExifOrientation),
            inspection.ExifOrientation,
            null);
    }

    private static ImageIntake Rejected(int statusCode, string kind, string detail) =>
        new(null, null, new ImageIntakeRejection(statusCode, kind, detail));

    /// <summary>
    /// Reads the one expected <c>file</c> section into bounded memory. Any second file section,
    /// any form field, or a missing file section is <c>400</c>; a section body that would pass the
    /// byte bound is <c>413</c>. Nothing on this path buffers to disk.
    /// </summary>
    private static async Task<BufferedImage> ReadSingleImageFileAsync(
        HttpRequest request,
        string boundary,
        string modeName,
        CancellationToken cancellationToken)
    {
        var reader = new MultipartReader(boundary, request.Body)
        {
            HeadersCountLimit = MaximumSectionHeaderCount,
            HeadersLengthLimit = MaximumSectionHeaderLength,
            BodyLengthLimit = MaximumImageBytes + MultipartOverheadAllowance,
        };

        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            {
                return BufferedImage.Refused(StatusCodes.Status400BadRequest, RequestInvalid,
                    "The multipart request carries a section without a usable content disposition.");
            }

            var isFileSection = disposition.FileName is not null || disposition.FileNameStar is not null;
            if (!isFileSection
                || !string.Equals(disposition.Name, "file", StringComparison.Ordinal))
            {
                return BufferedImage.Refused(StatusCodes.Status400BadRequest, RequestInvalid,
                    "Provide exactly one image in the 'file' form field.");
            }

            if (section.Body is null)
            {
                return BufferedImage.Refused(StatusCodes.Status400BadRequest, RequestInvalid,
                    "The 'file' form field carries no content.");
            }

            // Bounded in-memory buffering: the copy stops and answers 413 the moment the section
            // would pass the image byte bound, so memory is bounded by the constant no matter
            // what arrives.
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await section.Body.ReadAsync(chunk.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaximumImageBytes)
                {
                    return BufferedImage.Refused(StatusCodes.Status413PayloadTooLarge, InputTooLarge,
                        $"The image exceeds the {MaximumImageBytes}-byte {modeName} limit.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            if (buffer.Length == 0)
            {
                return BufferedImage.Refused(StatusCodes.Status400BadRequest, RequestInvalid,
                    "The 'file' form field is empty.");
            }

            // No section may follow the file: the boundary stream is read to its end, and any
            // further section is rejected without buffering it.
            if (await reader.ReadNextSectionAsync(cancellationToken) is not null)
            {
                return BufferedImage.Refused(StatusCodes.Status400BadRequest, RequestInvalid,
                    "Provide exactly one image in the 'file' form field.");
            }

            return new BufferedImage(
                new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, checked((int)buffer.Length)),
                section.ContentType,
                null);
        }

        return BufferedImage.Refused(StatusCodes.Status400BadRequest, RequestInvalid,
            "Provide exactly one image in the 'file' form field.");
    }

    private readonly record struct BufferedImage(
        ReadOnlyMemory<byte> Bytes,
        string? DeclaredMediaType,
        ImageIntakeRejection? Rejection)
    {
        public static BufferedImage Refused(int statusCode, string kind, string detail) =>
            new(ReadOnlyMemory<byte>.Empty, null, new ImageIntakeRejection(statusCode, kind, detail));
    }
}

/// <summary>A refused intake: the HTTP status, the code suffix, and the bounded detail.</summary>
internal sealed record ImageIntakeRejection(int StatusCode, string Kind, string Detail);

/// <summary>
/// An accepted intake (<see cref="Request"/> set, with the derived EXIF orientation) or a refusal
/// (<see cref="Rejection"/> set).
/// </summary>
internal sealed record ImageIntake(
    LayoutDetectionRequest? Request,
    int? Orientation,
    ImageIntakeRejection? Rejection);

/// <summary>
/// Bounds the request body to the fixed image bound plus the multipart overhead allowance, in the
/// same two-sided shape as the document upload: the server stops draining a body that cannot fit,
/// and the bounded copy while buffering remains the authority on what "too large" means for the
/// image itself.
/// </summary>
internal sealed class NonArchivingImageSizeLimitFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = checked(
                NonArchivingImageIntake.MaximumImageBytes + NonArchivingImageIntake.MultipartOverheadAllowance);
        }

        return next(context);
    }
}
