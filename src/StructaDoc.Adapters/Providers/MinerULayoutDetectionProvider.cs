using System.Net.Http.Headers;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

/// <summary>
/// The MinerU VLM layout adapter: it executes layout-only inference against the administrator
/// configured runtime and nothing else.
///
/// The adapter calls exactly one inference route — <see cref="LayoutRoutePath"/> beneath the
/// configured base URL — and that route is the wire protocol the layout inference runtime serves.
/// It never submits a parsing task, polls a task, recognizes region content, or generates
/// artifacts, and it must not be pointed at a full-parsing API: the runtime configured here is a
/// dedicated layout service, not a MinerU deployment doing double duty. Malformed or incomplete
/// runtime output is rejected with a bounded diagnostics error rather than answered as an empty
/// success.
/// </summary>
public sealed class MinerULayoutDetectionProvider(
    HttpClient layoutRuntimeClient,
    LayoutRuntimeConfiguration runtime) : ILayoutDetectionProvider
{
    /// <summary>The one route the adapter is allowed to call, relative to the configured base URL.</summary>
    public const string LayoutRoutePath = "v1/layout/detect";

    private readonly SemaphoreSlim concurrencyLimit = new(
        runtime.MaxConcurrency,
        runtime.MaxConcurrency);

    public async Task<LayoutDetectionResult> DetectLayoutAsync(
        LayoutDetectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRuntime(runtime);

        // The concurrency bound is held for the whole exchange, so a runtime that answers slowly
        // cannot accumulate unlimited in-flight work no matter how many callers arrive.
        await concurrencyLimit.WaitAsync(cancellationToken);
        try
        {
            using var requestMessage = new HttpRequestMessage(
                HttpMethod.Post,
                MinerUHttpProtocol.BuildEndpoint(runtime.BaseUri!, LayoutRoutePath))
            {
                Content = BuildRequestContent(request),
            };
            MinerUHttpProtocol.AddBearerCredential(
                requestMessage,
                runtime.Credential,
                required: false);

            using var response = await MinerUHttpProtocol.SendAsync(
                layoutRuntimeClient,
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                "layout",
                cancellationToken);
            MinerUHttpProtocol.EnsureSuccess(response, "layout");
            using var payload = await MinerUHttpProtocol.ReadJsonAsync(
                response,
                "layout",
                cancellationToken);
            return ParseResponse(payload.RootElement);
        }
        finally
        {
            concurrencyLimit.Release();
        }
    }

    private static void ValidateRuntime(LayoutRuntimeConfiguration runtime)
    {
        if (runtime.BaseUri is null)
        {
            throw new ProviderException(
                ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
                "No layout runtime Base URL is configured, so layout detection is unavailable.",
                ProviderFailureCategory.Configuration);
        }
    }

    /// <summary>
    /// Serialized up front for the same reason the full-parsing submissions are: the request carries
    /// a Content-Length and a bare <c>application/json</c> media type, and the image is written as
    /// base64 directly from the request bytes without an intermediate string copy.
    /// </summary>
    private static HttpContent BuildRequestContent(LayoutDetectionRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mediaType", request.MediaType);
            writer.WritePropertyName("image");
            writer.WriteBase64StringValue(request.Image.Span);
            writer.WriteEndObject();
            writer.Flush();
        }

        var content = new ByteArrayContent(
            buffer.GetBuffer(),
            0,
            checked((int)buffer.Length));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static LayoutDetectionResult ParseResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse("the response is not a JSON object.");
        }

        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!KnownResponseProperties.Contains(property.Name))
            {
                throw InvalidResponse($"the property '{property.Name}' is not part of the layout response.");
            }

            if (!seenProperties.Add(property.Name))
            {
                throw InvalidResponse($"the property '{property.Name}' is duplicated.");
            }
        }

        var width = ReadRequiredDimension(root, "width");
        var height = ReadRequiredDimension(root, "height");
        var regions = ReadRegions(root);

        try
        {
            return new LayoutDetectionResult(
                regions,
                width,
                height,
                ReadOptionalEngineString(root, "engine"),
                ReadOptionalEngineString(root, "model"),
                ReadOptionalEngineString(root, "version"));
        }
        catch (ArgumentException exception)
        {
            throw InvalidResponse(exception.Message);
        }
    }

    private static readonly HashSet<string> KnownResponseProperties = new(StringComparer.Ordinal)
    {
        "width",
        "height",
        "regions",
        "engine",
        "model",
        "version",
    };

    private static int ReadRequiredDimension(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var dimension))
        {
            throw InvalidResponse($"'{name}' is missing or is not an integer.");
        }

        if (dimension <= 0)
        {
            throw InvalidResponse($"'{name}' must be a positive pixel count.");
        }

        return dimension;
    }

    private static IReadOnlyList<LayoutDetectionRegion> ReadRegions(JsonElement root)
    {
        // A missing 'regions' is what a broken runtime's output looks like: rejecting it here is
        // the difference between a reported failure and a successful result that found nothing.
        if (!root.TryGetProperty("regions", out var regions)
            || regions.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse("'regions' is missing or is not an array.");
        }

        if (regions.GetArrayLength() > LayoutDetectionResult.MaximumRegionCount)
        {
            throw InvalidResponse(
                $"'regions' holds more than {LayoutDetectionResult.MaximumRegionCount} entries.");
        }

        var parsed = new List<LayoutDetectionRegion>(regions.GetArrayLength());
        var index = 0;
        foreach (var region in regions.EnumerateArray())
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse($"the region at index {index} is not an object.");
            }

            var seenRegionProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in region.EnumerateObject())
            {
                if (!KnownRegionProperties.Contains(property.Name))
                {
                    throw InvalidResponse(
                        $"the region at index {index} has an unknown property '{property.Name}'.");
                }

                if (!seenRegionProperties.Add(property.Name))
                {
                    throw InvalidResponse(
                        $"the region at index {index} duplicates the property '{property.Name}'.");
                }
            }

            var label = ReadRequiredString(region, "label", index);
            var x0 = ReadRequiredCoordinate(region, "x0", index);
            var y0 = ReadRequiredCoordinate(region, "y0", index);
            var x1 = ReadRequiredCoordinate(region, "x1", index);
            var y1 = ReadRequiredCoordinate(region, "y1", index);
            try
            {
                parsed.Add(new LayoutDetectionRegion(label, x0, y0, x1, y1));
            }
            catch (ArgumentException)
            {
                throw InvalidResponse(
                    $"the region at index {index} has a label or box outside the layout contract.");
            }

            index++;
        }

        return parsed;
    }

    private static readonly HashSet<string> KnownRegionProperties = new(StringComparer.Ordinal)
    {
        "label",
        "x0",
        "y0",
        "x1",
        "y1",
    };

    private static string ReadRequiredString(JsonElement region, string name, int index)
    {
        if (!region.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse($"the region at index {index} is missing the string '{name}'.");
        }

        return value.GetString()!;
    }

    private static double ReadRequiredCoordinate(JsonElement region, string name, int index)
    {
        // The kind is checked before the value because TryGetInt32 and GetDouble throw rather
        // than return false for kinds they cannot read, so the one malformed coordinate would
        // otherwise escape as an exception no Provider failure category applies to.
        if (!region.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw InvalidResponse(
                $"the region at index {index} is missing the number '{name}'.");
        }

        var coordinate = value.GetDouble();
        if (!double.IsFinite(coordinate) || coordinate is < 0 or > 1)
        {
            throw InvalidResponse(
                $"the region at index {index} has a '{name}' coordinate outside [0,1].");
        }

        return coordinate;
    }

    private static string? ReadOptionalEngineString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse($"'{name}' must be a string when the runtime supplies it.");
        }

        return value.GetString();
    }

    private static ProviderException InvalidResponse(string diagnostics) => new(
        "mineru-layout-response-invalid",
        $"The MinerU layout response was rejected: {diagnostics}",
        ProviderFailureCategory.Permanent);
}
