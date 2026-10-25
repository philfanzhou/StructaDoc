using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

internal static class MinerUHttpProtocol
{
    private const int MaximumJsonBytes = 1024 * 1024;

    public static void ValidateConfiguration(
        ProviderExecutionConfiguration configuration,
        string expectedProviderType,
        bool requireHttps)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.Equals(
                configuration.ProviderType,
                expectedProviderType,
                StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider-type-mismatch",
                "The Provider configuration type does not match the selected adapter.",
                ProviderFailureCategory.Configuration);
        }

        if (requireHttps && configuration.BaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ProviderException(
                "provider-https-required",
                "The remote Provider Base URL must use HTTPS.",
                ProviderFailureCategory.Configuration);
        }

        if (!string.IsNullOrEmpty(configuration.BaseUri.Query))
        {
            throw new ProviderException(
                "provider-base-url-query-unsupported",
                "The Provider Base URL cannot contain a query string.",
                ProviderFailureCategory.Configuration);
        }
    }

    /// <summary>
    /// A request body MinerU can read before it reads anything else: serialized up front so the
    /// request carries a Content-Length.
    ///
    /// <see cref="System.Net.Http.Json.JsonContent"/> cannot state a length, so HttpClient sends the
    /// body chunked. These payloads are a few hundred bytes and gain nothing from streaming, while a
    /// gateway that will not read a chunked request body hands the handler an empty one -- which
    /// arrives as a complaint about the first required field rather than about the framing, and is
    /// unrecognizable from the outside. The media type is written without a charset parameter for
    /// the same reason: it is what the Provider documents, and its own parameter-error code covers
    /// an unexpected Content-Type.
    /// </summary>
    public static HttpContent CreateJsonContent<TPayload>(TPayload payload)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    public static Uri BuildEndpoint(Uri baseUri, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var builder = new UriBuilder(baseUri)
        {
            Path = $"{baseUri.AbsolutePath.TrimEnd('/')}/{relativePath.TrimStart('/')}",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    public static string ValidateExternalTaskId(string externalTaskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTaskId);
        if (externalTaskId.Length > 512
            || !string.Equals(externalTaskId, externalTaskId.Trim(), StringComparison.Ordinal)
            || externalTaskId.Any(char.IsControl))
        {
            throw new ProviderException(
                "provider-external-task-id-invalid",
                "The Provider external task ID is invalid.",
                ProviderFailureCategory.Permanent);
        }

        return Uri.EscapeDataString(externalTaskId);
    }

    public static string ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Length > 255
            || fileName is "." or ".."
            || !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal)
            || fileName.Any(char.IsControl)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider-source-file-name-invalid",
                "The source file name is not safe for Provider submission.",
                ProviderFailureCategory.Input);
        }

        return fileName;
    }

    public static void ValidateSource(
        ProviderDocumentSource source,
        ProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capabilities);

        bool supportsMediaType;
        try
        {
            supportsMediaType = capabilities.SupportsMediaType(source.MediaType);
        }
        catch (ArgumentException exception)
        {
            throw new ProviderException(
                "provider-source-media-type-invalid",
                "The source document media type is invalid.",
                ProviderFailureCategory.Input,
                exception);
        }

        if (!supportsMediaType)
        {
            throw new ProviderException(
                "provider-source-media-type-unsupported",
                "The source document media type is not supported by the Provider.",
                ProviderFailureCategory.Input);
        }

        if (capabilities.MaxFileBytes.HasValue
            && source.SizeBytes > capabilities.MaxFileBytes.Value)
        {
            throw new ProviderException(
                "provider-source-file-too-large",
                "The source document exceeds the Provider file size limit.",
                ProviderFailureCategory.Input);
        }
    }

    public static void AddBearerCredential(
        HttpRequestMessage request,
        ProviderCredential? credential,
        bool required)
    {
        if (credential is null)
        {
            if (required)
            {
                throw new ProviderException(
                    "provider-credential-required",
                    "The Provider requires a credential.",
                    ProviderFailureCategory.Configuration);
            }

            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            credential.Reveal());
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                request,
                completionOption,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(
                $"mineru-{operation}-timeout",
                $"The MinerU {operation} request timed out.",
                ProviderFailureCategory.Transient);
        }
        catch (HttpRequestException exception) when (HasSignedTransferSecurityFailure(exception))
        {
            throw new ProviderException(
                $"mineru-{operation}-destination-denied",
                $"The MinerU {operation} destination was denied by the outbound policy.",
                ProviderFailureCategory.Security,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException(
                $"mineru-{operation}-network-error",
                $"The MinerU {operation} request failed due to a network error.",
                ProviderFailureCategory.Transient,
                exception);
        }
    }

    private static bool HasSignedTransferSecurityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SignedTransferSecurityException)
            {
                return true;
            }
        }

        return false;
    }

    public static void EnsureSuccess(
        HttpResponseMessage response,
        string operation,
        ProviderFailureCategory unauthorizedCategory = ProviderFailureCategory.Configuration)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var statusCode = response.StatusCode;
        var category = statusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                ProviderFailureCategory.Input,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                unauthorizedCategory,
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests =>
                ProviderFailureCategory.Transient,
            >= HttpStatusCode.InternalServerError => ProviderFailureCategory.Transient,
            _ => ProviderFailureCategory.Permanent,
        };

        throw new ProviderException(
            $"mineru-{operation}-http-{(int)statusCode}",
            $"The MinerU {operation} request returned HTTP {(int)statusCode}.",
            category);
    }

    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    var read = await content.ReadAsync(rented, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length > MaximumJsonBytes - read)
                    {
                        throw new ProviderException(
                            $"mineru-{operation}-response-too-large",
                            $"The MinerU {operation} response exceeded the JSON size limit.",
                            ProviderFailureCategory.Permanent);
                    }

                    await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                $"mineru-{operation}-response-invalid",
                $"The MinerU {operation} response is not valid JSON.",
                ProviderFailureCategory.Permanent,
                exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(
                $"mineru-{operation}-response-timeout",
                $"The MinerU {operation} response timed out.",
                ProviderFailureCategory.Transient);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new ProviderException(
                $"mineru-{operation}-response-network-error",
                $"The MinerU {operation} response failed due to a network error.",
                ProviderFailureCategory.Transient,
                exception);
        }
    }

    public static Uri ValidateSignedUri(string? value, string operation)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsoluteUri.Length > 4096)
        {
            throw new ProviderException(
                $"mineru-{operation}-url-invalid",
                $"The MinerU {operation} URL is invalid.",
                ProviderFailureCategory.Security);
        }

        return uri;
    }

    /// <summary>
    /// Reads the <c>data</c> object of a successful MinerU Cloud JSON envelope, where success is a
    /// numeric <c>code</c> of zero — the Provider answers some refusals with HTTP 200 and a
    /// non-zero code, so the envelope itself is what decides.
    /// </summary>
    /// <remarks>
    /// The kind is checked before the value because TryGetInt32 throws rather than returning false
    /// when the element is not a number, and MinerU spells some of its error codes as strings.
    /// Left unguarded, the one response that carries a reason is the one that crashes the adapter,
    /// and it escapes as an exception no Provider failure category applies to.
    /// </remarks>
    public static JsonElement ReadSuccessfulData(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.Number
            || !code.TryGetInt32(out var codeValue)
            || codeValue != 0)
        {
            // The reason is the Provider's to give and the only part of this failure the service
            // cannot work out for itself. Without it the caller records that MinerU said no and
            // nothing about why, which is indistinguishable from the service being broken -- an
            // expired token, an unverified account, and an exhausted quota all look the same.
            throw new ProviderException(
                $"mineru-{operation}-rejected",
                $"The MinerU {operation} request was rejected by the Provider. {DescribeRejection(root)}",
                ProviderFailureCategory.Permanent);
        }

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            throw InvalidCloudResponse(
                operation,
                $"The MinerU {operation} response is missing 'data'.");
        }

        return data;
    }

    public static ProviderException InvalidCloudResponse(string operation, string message) => new(
        $"mineru-{operation}-response-invalid",
        message,
        ProviderFailureCategory.Permanent);

    public static ProviderException InvalidCloudResponse(
        string operation,
        string message,
        ProviderFailureCategory category) => new(
        $"mineru-{operation}-response-invalid",
        message,
        category);

    public static ProviderException InvalidCloudResponse(
        string operation,
        string message,
        ProviderFailureCategory category,
        Exception innerException) => new(
        $"mineru-{operation}-response-invalid",
        message,
        category,
        innerException);

    public static string ReadRequiredCloudString(
        JsonElement root,
        string name,
        string operation)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw InvalidCloudResponse(
                operation,
                $"The MinerU {operation} response is missing '{name}'.");
        }

        return value.GetString()!;
    }

    /// <summary>
    /// What the Provider said when it refused, in the fields that say it: its own code, its message,
    /// and the trace identifier its support asks for.
    ///
    /// Only those. A successful response from these same endpoints carries presigned URLs, so
    /// echoing a whole body would put a credential-bearing URL into an error path the first time
    /// the shape of a rejection changed.
    ///
    /// Each of the three is spelled two ways because the Provider spells them two ways: its gateway
    /// answers with <c>msgCode</c> and <c>traceId</c>, its extraction API with <c>code</c> and
    /// <c>trace_id</c>. Reading only one spelling loses the trace identifier from half of the
    /// rejections, which is the one field that lets somebody else look the failure up.
    /// </summary>
    public static string DescribeRejection(JsonElement root)
    {
        string[] names = ["code", "msgCode", "msg", "trace_id", "traceId"];
        var described = names
            .Select(name => (Name: name, Value: ReadRejectionScalar(root, name)))
            .Where(field => field.Value is not null)
            .Select(field => $"{field.Name}={field.Value}")
            .ToArray();

        return described.Length == 0
            ? "It gave no code or message."
            : string.Join(", ", described);
    }

    private const int MaximumRejectionDetailLength = 200;

    private static string? ReadRejectionScalar(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Someone else's string, on its way into a stored error and onto a page. Control characters
        // and an unbounded length are what a foreign string must not bring with it; error paths are
        // bounded too, and truncating here keeps the rest of the sentence.
        var sanitized = new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (sanitized.Length == 0)
        {
            return null;
        }

        return sanitized.Length > MaximumRejectionDetailLength
            ? string.Concat(sanitized.AsSpan(0, MaximumRejectionDetailLength), "…")
            : sanitized;
    }
}
