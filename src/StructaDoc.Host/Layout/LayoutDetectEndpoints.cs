using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using ServiceMantle.Web.Http;
using StructaDoc.Application.Providers;
using StructaDoc.Contracts.Layout;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Layout;

/// <summary>
/// The public non-archiving layout-detection endpoint (ADR-0013, backend selection per ADR-0014):
/// one JPEG or PNG page image in, normalized regions out, in the same request, with nothing
/// written anywhere.
///
/// The endpoint owns the parts the execution contract leaves to its caller — authorization
/// (through the API-client-only <c>layout:detect</c> policy), request validation against the
/// actual bytes, bounded buffering, overload, and the deadline, which is the caller's cancellation
/// token plus the selected backend's own bound (the runtime's configured timeout or the cloud
/// route's overall deadline) — and maps every Provider failure to a stable HTTP outcome. It reads
/// the multipart body itself rather than through form binding, because form binding spills large
/// file sections to temporary files and this path creates none (ADR-0013 point 1 is satisfied
/// vacuously: nothing to clean up because nothing is written).
/// </summary>
public static class LayoutDetectEndpoints
{
    /// <summary>The multipart overhead allowance, in the same shape as the document upload bound.</summary>
    private const long MultipartOverheadAllowance = 1024 * 1024;

    private const int MaximumImageBytes = LayoutDetectionRequest.MaximumImageBytes;

    private const int MaximumSectionHeaderCount = 16;

    private const int MaximumSectionHeaderLength = 4096;

    /// <summary>
    /// The stable code of the adapters' deadline failure for the layout route (see MinerU HTTP
    /// Providers, "MinerU Layout Runtime" and "MinerU Cloud Layout Mode"): the one Provider error
    /// code the public mapping looks at individually, because an expired deadline answers
    /// <c>504</c> rather than the generic upstream-unavailable <c>502</c>.
    /// </summary>
    private const string LayoutTimeoutErrorCode = "mineru-layout-timeout";

    public static IEndpointRouteBuilder MapLayoutDetectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/layout/detect", DetectLayoutAsync)
            .WithName("DetectLayout")
            // No `Accepts` metadata here, unlike the document upload: that metadata makes the
            // platform answer a non-multipart request with its own 415 before the handler runs,
            // while this endpoint's settled contract answers 400 `layout-request-invalid` for a
            // request that is not multipart at all and reserves 415 for a media-type problem with
            // the image itself. The request body is described explicitly by the OpenAPI contract
            // transformer instead.
            .Produces<LayoutDetectResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .RequireAuthorization(AuthorizationPolicies.LayoutDetect)
            .AddEndpointFilter(new LayoutRequestSizeLimitFilter());
        return endpoints;
    }

    private static async Task<IResult> DetectLayoutAsync(
        HttpRequest request,
        ILayoutDetectionProviderResolver resolver,
        LayoutBackendSelection backendSelection,
        LayoutDetectInFlightGate inFlightGate,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // One completion event per request, and nothing else on the path logs: it carries the
        // request's correlation identifier (from the log scope the correlation middleware opens),
        // the elapsed time, the region count, and the status — never image bytes, detected
        // content, or anything the backend said (ADR-0013 point 5). The category is a literal
        // because the endpoint class is static and cannot be the logger's type argument.
        var logger = loggerFactory.CreateLogger("StructaDoc.Host.Layout.LayoutDetectEndpoints");
        var stopwatch = Stopwatch.StartNew();
        LayoutOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(
                request,
                resolver,
                backendSelection,
                inFlightGate,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: the connection closes with no response written. Cancellation
            // propagates rather than becoming a Provider error, per the execution contract.
            stopwatch.Stop();
            logger.LogWarning(
                "Layout detection was cancelled by the caller after {ElapsedMilliseconds} ms.",
                stopwatch.ElapsedMilliseconds);
            throw;
        }

        stopwatch.Stop();
        logger.LogInformation(
            "Layout detection ended with status {Status} after {ElapsedMilliseconds} ms and {RegionCount} regions.",
            outcome.Status,
            stopwatch.ElapsedMilliseconds,
            outcome.RegionCount);
        return outcome.Result;
    }

    private static async Task<LayoutOutcome> ExecuteAsync(
        HttpRequest request,
        ILayoutDetectionProviderResolver resolver,
        LayoutBackendSelection backendSelection,
        LayoutDetectInFlightGate inFlightGate,
        CancellationToken cancellationToken)
    {
        // Everything about the request shape is decided before the body is read or a slot is
        // taken: content type, then the multipart boundary.
        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var requestMediaType)
            || requestMediaType!.MediaType is not { } requestMediaTypeName
            || !requestMediaTypeName.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return LayoutOutcome.FromProblem(
                StatusCodes.Status400BadRequest,
                "layout-request-invalid",
                "The request must use multipart/form-data.");
        }

        var boundary = request.GetMultipartBoundary();
        if (string.IsNullOrEmpty(boundary))
        {
            return LayoutOutcome.FromProblem(
                StatusCodes.Status400BadRequest,
                "layout-request-invalid",
                "The multipart request carries no boundary.");
        }

        // The cap is entered before the body is read, which is what bounds the memory the
        // endpoint can be holding at once, and it rejects rather than queues.
        if (!inFlightGate.TryEnter())
        {
            request.HttpContext.Response.Headers.RetryAfter =
                LayoutDetectInFlightGate.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return LayoutOutcome.FromProblem(
                StatusCodes.Status429TooManyRequests,
                "layout-overloaded",
                "Too many layout detection requests are in flight; retry after the suggested delay.");
        }

        try
        {
            var image = await ReadSingleImageFileAsync(request, boundary, cancellationToken);
            if (image.Problem is not null)
            {
                return image.Problem;
            }

            var inspection = LayoutImageInspection.Parse(image.Bytes.Span);
            if (inspection.Verdict == LayoutImageVerdict.Unsupported)
            {
                return LayoutOutcome.FromProblem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "layout-media-type-unsupported",
                    "The submitted image must be a JPEG or a PNG.");
            }

            if (inspection.Verdict == LayoutImageVerdict.PixelBoundExceeded)
            {
                return LayoutOutcome.FromProblem(
                    StatusCodes.Status413PayloadTooLarge,
                    "layout-input-too-large",
                    $"The image exceeds the pixel bounds of {LayoutImageInspection.MaximumDimensionPixels} "
                        + $"per dimension and {LayoutImageInspection.MaximumTotalPixels} in total.");
            }

            // The signature has decided the format. A declared content type that contradicts it —
            // or names an unsupported format at all — is refused rather than trusted.
            if (image.DeclaredMediaType is { } declared
                && (!MediaTypeHeaderValue.TryParse(declared, out var declaredMediaType)
                    || declaredMediaType!.MediaType is not { } declaredName
                    || !declaredName.Equals(inspection.MediaType, StringComparison.OrdinalIgnoreCase)))
            {
                return LayoutOutcome.FromProblem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "layout-media-type-unsupported",
                    $"The declared content type does not match the submitted {inspection.MediaType} image.");
            }

            // Capability first: whichever backend the administrator selected answers its own
            // unsupported state here — an unconfigured runtime, or a cloud route whose default
            // Provider is absent, disabled, not a cloud one, or credential-less — before any bytes
            // are handed to an execution.
            var provider = resolver.Resolve(backendSelection);
            var result = await provider.DetectLayoutAsync(
                new LayoutDetectionRequest(
                    image.Bytes,
                    inspection.MediaType,
                    inspection.StoredWidth,
                    inspection.StoredHeight,
                    inspection.ExifOrientation),
                cancellationToken);

            request.HttpContext.TryGetServiceMantleCorrelationId(out var correlationId);
            var response = new LayoutDetectResponse(
                correlationId ?? string.Empty,
                result.SourceImageWidth,
                result.SourceImageHeight,
                LayoutDetectResponse.CanonicalCoordinateFrame,
                result.Regions
                    .Select((region, index) => new LayoutDetectRegion(
                        index,
                        region.Label,
                        region.X0,
                        region.Y0,
                        region.X1,
                        region.Y1))
                    .ToArray())
            {
                Orientation = inspection.ExifOrientation,
                Engine = result.Engine,
                Model = result.Model,
                Version = result.Version,
                Diagnostics = result.Diagnostics,
            };

            // An explicitly empty region list is a real answer — the runtime looked and found
            // nothing — and is carried as such rather than turned into an error.
            return new LayoutOutcome(Results.Ok(response), StatusCodes.Status200OK, result.Regions.Count);
        }
        catch (ProviderException exception)
        {
            return MapProviderFailure(exception);
        }
        finally
        {
            inFlightGate.Release();
        }
    }

    /// <summary>
    /// Reads the one expected <c>file</c> section into bounded memory. Any second file section,
    /// any form field, or a missing file section answers <c>400</c>; a section body that would
    /// pass the byte bound answers <c>413</c>. Nothing on this path buffers to disk.
    /// </summary>
    private static async Task<LayoutImage> ReadSingleImageFileAsync(
        HttpRequest request,
        string boundary,
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
                return LayoutImage.Rejected(StatusCodes.Status400BadRequest, "layout-request-invalid",
                    "The multipart request carries a section without a usable content disposition.");
            }

            var isFileSection = disposition.FileName is not null || disposition.FileNameStar is not null;
            if (!isFileSection
                || !string.Equals(disposition.Name, "file", StringComparison.Ordinal))
            {
                return LayoutImage.Rejected(StatusCodes.Status400BadRequest, "layout-request-invalid",
                    "Provide exactly one image in the 'file' form field.");
            }

            if (section.Body is null)
            {
                return LayoutImage.Rejected(StatusCodes.Status400BadRequest, "layout-request-invalid",
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
                    return LayoutImage.Rejected(StatusCodes.Status413PayloadTooLarge, "layout-input-too-large",
                        $"The image exceeds the {MaximumImageBytes}-byte layout detection limit.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            if (buffer.Length == 0)
            {
                return LayoutImage.Rejected(StatusCodes.Status400BadRequest, "layout-request-invalid",
                    "The 'file' form field is empty.");
            }

            // No section may follow the file: the boundary stream is read to its end, and any
            // further section is rejected without buffering it.
            if (await reader.ReadNextSectionAsync(cancellationToken) is not null)
            {
                return LayoutImage.Rejected(StatusCodes.Status400BadRequest, "layout-request-invalid",
                    "Provide exactly one image in the 'file' form field.");
            }

            return new LayoutImage(
                new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, checked((int)buffer.Length)),
                section.ContentType);
        }

        return LayoutImage.Rejected(StatusCodes.Status400BadRequest, "layout-request-invalid",
            "Provide exactly one image in the 'file' form field.");
    }

    /// <summary>
    /// The settled Provider-failure mapping: every outcome is stable and provider-neutral, and
    /// every category is covered, so no Provider failure can escape as an unhandled 500.
    /// </summary>
    private static LayoutOutcome MapProviderFailure(ProviderException exception)
    {
        if (exception.ErrorCode == ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode)
        {
            return LayoutOutcome.FromProblem(
                StatusCodes.Status503ServiceUnavailable,
                "layout-capability-unsupported",
                exception.Message);
        }

        if (exception.ErrorCode == LayoutTimeoutErrorCode)
        {
            return LayoutOutcome.FromProblem(
                StatusCodes.Status504GatewayTimeout,
                "layout-timeout",
                exception.Message);
        }

        return exception.Category switch
        {
            // Malformed or oversized runtime output: retrying the same request will not help,
            // and the runtime's answer is not usable.
            ProviderFailureCategory.Permanent => LayoutOutcome.FromProblem(
                StatusCodes.Status502BadGateway, "layout-output-invalid", exception.Message),

            // Network and other transient upstream failures.
            ProviderFailureCategory.Transient => LayoutOutcome.FromProblem(
                StatusCodes.Status502BadGateway, "layout-runtime-unavailable", exception.Message),

            // The configured runtime refused the exchange itself, for example its credential:
            // an administration matter, so it is reported on this service's availability.
            ProviderFailureCategory.Configuration => LayoutOutcome.FromProblem(
                StatusCodes.Status503ServiceUnavailable, "layout-runtime-unavailable", exception.Message),

            // The runtime rejected the forwarded image. The image already satisfied this
            // service's own validation, so the refusal is an upstream behavior, not something
            // the caller can repair by changing the request.
            ProviderFailureCategory.Input => LayoutOutcome.FromProblem(
                StatusCodes.Status502BadGateway, "layout-runtime-rejected", exception.Message),

            // Anything else — an outbound security guard, or a category added later — is
            // deterministic rather than a 500.
            _ => LayoutOutcome.FromProblem(
                StatusCodes.Status503ServiceUnavailable, "layout-runtime-unavailable", exception.Message),
        };
    }

    private static IResult LayoutProblem(int statusCode, string code, string detail)
    {
        return Results.Problem(
            statusCode: statusCode,
            title: "Layout detection failed",
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });
    }

    /// <summary>
    /// Bounds the request body to the fixed layout image bound plus the multipart overhead
    /// allowance, in the same two-sided shape as the document upload: the server stops draining a
    /// body that cannot fit, and the bounded copy while buffering remains the authority on what
    /// "too large" means for the image itself.
    /// </summary>
    private sealed class LayoutRequestSizeLimitFilter : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(
            EndpointFilterInvocationContext context,
            EndpointFilterDelegate next)
        {
            var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = checked(MaximumImageBytes + MultipartOverheadAllowance);
            }

            return next(context);
        }
    }

    private sealed record LayoutOutcome(IResult Result, int Status, int RegionCount)
    {
        public static LayoutOutcome FromProblem(int statusCode, string code, string detail) =>
            new(LayoutProblem(statusCode, code, detail), statusCode, 0);
    }

    private readonly record struct LayoutImage(ReadOnlyMemory<byte> Bytes, string? DeclaredMediaType)
    {
        public LayoutOutcome? Problem { get; init; }

        public static LayoutImage Rejected(int statusCode, string code, string detail) =>
            new(ReadOnlyMemory<byte>.Empty, null)
            {
                Problem = new LayoutOutcome(
                    LayoutProblem(statusCode, code, detail),
                    statusCode,
                    0),
            };
    }
}
