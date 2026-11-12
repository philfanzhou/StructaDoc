using System.Diagnostics;
using System.Globalization;
using ServiceMantle.Web.Http;
using StructaDoc.Application.Providers;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Contracts.Recognition;
using StructaDoc.Host.Authentication;
using StructaDoc.Host.Layout;

namespace StructaDoc.Host.Recognition;

/// <summary>
/// The public non-archiving image text-recognition endpoint (ADR-0015): one JPEG or PNG page image
/// in, recognized blocks with lines and normalized boxes out, in the same request, with nothing
/// written anywhere.
///
/// It takes the same input under the same rules as the layout endpoint (the shared
/// <see cref="NonArchivingImageIntake"/>), behind its own API-client-only scope, its own in-flight
/// cap, and its own problem-code family. The mode is off unless an administrator turns it on, and
/// only the configured MinerU Cloud Provider serves it; the deadline is the caller's cancellation
/// token plus the cloud exchange's overall bound.
/// </summary>
public static class ImageRecognizeEndpoints
{
    /// <summary>
    /// The stable code of the cloud adapter's deadline failure for this route: the one Provider
    /// error code the public mapping looks at individually, because an expired deadline answers
    /// <c>504</c> rather than the generic upstream-unavailable <c>502</c>.
    /// </summary>
    private const string RecognitionTimeoutErrorCode = "mineru-recognition-timeout";

    public static IEndpointRouteBuilder MapImageRecognizeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/image/recognize", RecognizeAsync)
            .WithName("RecognizeImage")
            // No `Accepts` metadata, for the layout endpoint's reason: a request that is not
            // multipart at all answers 400 `recognition-request-invalid`, and 415 is reserved for
            // the image itself. The OpenAPI contract transformer describes the request body.
            .Produces<ImageRecognizeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .RequireAuthorization(AuthorizationPolicies.ImageRecognize)
            .AddEndpointFilter(new NonArchivingImageSizeLimitFilter());
        return endpoints;
    }

    private static async Task<IResult> RecognizeAsync(
        HttpRequest request,
        IImageRecognitionProviderResolver resolver,
        ImageRecognizeInFlightGate inFlightGate,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // One completion event per request, and nothing else on the path logs: the correlation
        // identifier (from the log scope), the elapsed time, the block and line counts, and the
        // status — never image bytes, recognized text, raw responses, credentials, or signed URLs
        // (ADR-0015 point 5).
        var logger = loggerFactory.CreateLogger("StructaDoc.Host.Recognition.ImageRecognizeEndpoints");
        var stopwatch = Stopwatch.StartNew();
        RecognitionOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(request, resolver, inFlightGate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            logger.LogWarning(
                "Image recognition was cancelled by the caller after {ElapsedMilliseconds} ms.",
                stopwatch.ElapsedMilliseconds);
            throw;
        }

        stopwatch.Stop();
        logger.LogInformation(
            "Image recognition ended with status {Status} after {ElapsedMilliseconds} ms, {BlockCount} blocks, and {LineCount} lines.",
            outcome.Status,
            stopwatch.ElapsedMilliseconds,
            outcome.BlockCount,
            outcome.LineCount);
        return outcome.Result;
    }

    private static async Task<RecognitionOutcome> ExecuteAsync(
        HttpRequest request,
        IImageRecognitionProviderResolver resolver,
        ImageRecognizeInFlightGate inFlightGate,
        CancellationToken cancellationToken)
    {
        if (NonArchivingImageIntake.CheckShape(request, out var boundary) is { } shapeRejection)
        {
            return RecognitionOutcome.FromRejection(shapeRejection);
        }

        // The cap is entered before the body is read, which is what bounds the memory the
        // endpoint can be holding at once, and it rejects rather than queues.
        if (!inFlightGate.TryEnter())
        {
            request.HttpContext.Response.Headers.RetryAfter =
                ImageRecognizeInFlightGate.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return RecognitionOutcome.FromProblem(
                StatusCodes.Status429TooManyRequests,
                "recognition-overloaded",
                "Too many image recognition requests are in flight; retry after the suggested delay.");
        }

        try
        {
            var image = await NonArchivingImageIntake.ReadAsync(
                request,
                boundary,
                "image recognition",
                cancellationToken);
            if (image.Rejection is { } rejection)
            {
                return RecognitionOutcome.FromRejection(rejection);
            }

            // Capability first: a deployment with the mode off answers here, before any bytes are
            // handed to an execution, and a cloud route whose default Provider is absent, disabled,
            // not a cloud one, or credential-less answers from the adapter before it sends anything.
            var provider = resolver.Resolve();
            var result = await provider.RecognizeAsync(image.Request!, cancellationToken);

            request.HttpContext.TryGetServiceMantleCorrelationId(out var correlationId);
            var response = new ImageRecognizeResponse(
                correlationId ?? string.Empty,
                result.SourceImageWidth,
                result.SourceImageHeight,
                ImageRecognizeResponse.CanonicalCoordinateFrame,
                result.Blocks
                    .Select((block, index) => new ImageRecognizeBlock(
                        index,
                        block.Type,
                        block.Content,
                        block.ContentFormat,
                        ToResponse(block.Box),
                        block.Lines
                            .Select(line => new ImageRecognizeLine(line.Text, ToResponse(line.Box)))
                            .ToArray())
                    {
                        Caption = block.Caption,
                        Footnote = block.Footnote,
                    })
                    .ToArray())
            {
                Orientation = image.Orientation,
                Engine = result.Engine,
                Model = result.Model,
                Version = result.Version,
                Diagnostics = result.Diagnostics,
            };

            // An empty block list is a real answer — the backend looked and found nothing.
            return new RecognitionOutcome(
                Results.Ok(response),
                StatusCodes.Status200OK,
                result.Blocks.Count,
                result.Blocks.Sum(block => block.Lines.Count));
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

    private static BoundingBoxResponse ToResponse(ImageRecognitionBox box) =>
        new(box.X0, box.Y0, box.X1, box.Y1);

    /// <summary>
    /// The Provider-failure mapping, the layout route's outcome model under this route's codes:
    /// every category is covered, so no Provider failure can escape as an unhandled 500.
    /// </summary>
    private static RecognitionOutcome MapProviderFailure(ProviderException exception)
    {
        if (exception.ErrorCode == IImageRecognitionProviderResolver.UnsupportedCapabilityErrorCode)
        {
            return RecognitionOutcome.FromProblem(
                StatusCodes.Status503ServiceUnavailable,
                "recognition-capability-unsupported",
                exception.Message);
        }

        if (exception.ErrorCode == RecognitionTimeoutErrorCode)
        {
            return RecognitionOutcome.FromProblem(
                StatusCodes.Status504GatewayTimeout,
                "recognition-timeout",
                exception.Message);
        }

        return exception.Category switch
        {
            // Malformed, incompatible, or oversized backend output, or a refused extraction:
            // retrying the same request will not help.
            ProviderFailureCategory.Permanent => RecognitionOutcome.FromProblem(
                StatusCodes.Status502BadGateway, "recognition-output-invalid", exception.Message),

            ProviderFailureCategory.Transient => RecognitionOutcome.FromProblem(
                StatusCodes.Status502BadGateway, "recognition-backend-unavailable", exception.Message),

            // The cloud refused the exchange itself, for example its credential: an
            // administration matter, reported on this service's availability.
            ProviderFailureCategory.Configuration => RecognitionOutcome.FromProblem(
                StatusCodes.Status503ServiceUnavailable, "recognition-backend-unavailable", exception.Message),

            // The cloud rejected an image this service had already accepted: an upstream
            // behavior the caller cannot repair by changing the request.
            ProviderFailureCategory.Input => RecognitionOutcome.FromProblem(
                StatusCodes.Status502BadGateway, "recognition-backend-rejected", exception.Message),

            _ => RecognitionOutcome.FromProblem(
                StatusCodes.Status503ServiceUnavailable, "recognition-backend-unavailable", exception.Message),
        };
    }

    private static IResult RecognitionProblem(int statusCode, string code, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: "Image recognition failed",
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });

    private sealed record RecognitionOutcome(IResult Result, int Status, int BlockCount, int LineCount)
    {
        public static RecognitionOutcome FromProblem(int statusCode, string code, string detail) =>
            new(RecognitionProblem(statusCode, code, detail), statusCode, 0, 0);

        public static RecognitionOutcome FromRejection(ImageIntakeRejection rejection) =>
            FromProblem(rejection.StatusCode, $"recognition-{rejection.Kind}", rejection.Detail);
    }
}
