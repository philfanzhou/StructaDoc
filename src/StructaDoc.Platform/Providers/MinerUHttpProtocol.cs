using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Platform.Providers;

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
}
