using System.Net.Http.Json;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Infrastructure.Providers;

public sealed class MinerUCloudParseProvider(
    HttpClient providerApiClient,
    HttpClient signedTransferClient) : IParseProvider
{
    private static readonly ProviderCapabilities Capabilities = new(
        [
            "application/pdf",
            "image/png",
            "image/jpeg",
            "image/jp2",
            "image/webp",
            "image/gif",
            "image/bmp",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "text/html",
        ],
        maxFileBytes: 200L * 1024 * 1024,
        maxPages: 600,
        supportsCancellation: false);

    public string ProviderType => ProviderTypes.MinerUCloud;

    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        ProviderExecutionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: true);
        RequireCredential(configuration);
        return Task.FromResult(Capabilities);
    }

    public async Task<ProviderSubmissionCheckpoint?> PrepareSubmissionAsync(
        ProviderExecutionConfiguration configuration,
        Guid parseRunId,
        ProviderDocumentSource source,
        string optionsJson,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: true);
        if (parseRunId == Guid.Empty)
        {
            throw new ArgumentException("A Parse Run ID is required.", nameof(parseRunId));
        }

        ArgumentNullException.ThrowIfNull(source);
        MinerUHttpProtocol.ValidateSource(source, Capabilities);
        var fileName = MinerUHttpProtocol.ValidateFileName(source.FileName);
        var options = MinerUProviderOptions.Parse(optionsJson);
        options.ValidateForCloud();
        var dataId = parseRunId.ToString("N");
        var requestPayload = BuildSubmissionPayload(
            configuration,
            options,
            fileName,
            dataId);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            MinerUHttpProtocol.BuildEndpoint(
                configuration.BaseUri,
                "api/v4/file-urls/batch"))
        {
            Content = JsonContent.Create(requestPayload),
        };
        MinerUHttpProtocol.AddBearerCredential(
            request,
            configuration.Credential,
            required: true);

        string batchId;
        Uri uploadUri;
        using (var response = await MinerUHttpProtocol.SendAsync(
                   providerApiClient,
                   request,
                   HttpCompletionOption.ResponseHeadersRead,
                   "cloud-submit",
                   cancellationToken))
        {
            MinerUHttpProtocol.EnsureSuccess(response, "cloud-submit");
            using var payload = await MinerUHttpProtocol.ReadJsonAsync(
                response,
                "cloud-submit",
                cancellationToken);
            var data = ReadSuccessfulData(payload.RootElement, "cloud-submit");
            batchId = ReadRequiredString(data, "batch_id", "cloud-submit");
            MinerUHttpProtocol.ValidateExternalTaskId(batchId);
            if (!data.TryGetProperty("file_urls", out var fileUrls)
                || fileUrls.ValueKind != JsonValueKind.Array
                || fileUrls.GetArrayLength() != 1
                || fileUrls[0].ValueKind != JsonValueKind.String)
            {
                throw InvalidResponse(
                    "cloud-submit",
                    "The MinerU Cloud submission response did not contain one upload URL.");
            }

            uploadUri = MinerUHttpProtocol.ValidateSignedUri(
                fileUrls[0].GetString(),
                "cloud-upload");
        }

        return new ProviderSubmissionCheckpoint(batchId, uploadUri.AbsoluteUri);
    }

    public async Task<ProviderSubmission> SubmitAsync(
        ProviderExecutionConfiguration configuration,
        Guid parseRunId,
        ProviderDocumentSource source,
        string optionsJson,
        ProviderSubmissionCheckpoint? checkpoint,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: true);
        if (parseRunId == Guid.Empty)
        {
            throw new ArgumentException("A Parse Run ID is required.", nameof(parseRunId));
        }

        ArgumentNullException.ThrowIfNull(source);
        MinerUHttpProtocol.ValidateSource(source, Capabilities);
        MinerUProviderOptions.Parse(optionsJson).ValidateForCloud();

        if (checkpoint is null)
        {
            throw new ProviderException(
                "mineru-cloud-checkpoint-required",
                "MinerU Cloud requires a durable submission checkpoint before upload.",
                ProviderFailureCategory.Configuration);
        }

        MinerUHttpProtocol.ValidateExternalTaskId(checkpoint.ExternalTaskId);
        var uploadUri = MinerUHttpProtocol.ValidateSignedUri(
            checkpoint.ContinuationToken,
            "cloud-upload");
        var result = await GetBatchResultAsync(
            configuration,
            checkpoint.ExternalTaskId,
            cancellationToken);

        switch (result.State)
        {
            case "waiting-file":
                await UploadSourceAsync(source, uploadUri, cancellationToken);
                break;

            case "pending":
            case "converting":
            case "running":
            case "done":
                break;

            case "failed":
                throw new ProviderException(
                    "mineru-cloud-task-failed",
                    "The MinerU Cloud task failed.",
                    ProviderFailureCategory.Permanent);

            default:
                throw new ProviderException(
                    "mineru-cloud-submit-state-unknown",
                    "The MinerU Cloud task returned an unknown submission state.",
                    ProviderFailureCategory.Transient);
        }

        return new ProviderSubmission(checkpoint.ExternalTaskId, TimeSpan.FromSeconds(5));
    }

    public async Task<ProviderTaskStatus> GetStatusAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetBatchResultAsync(
            configuration,
            externalTaskId,
            cancellationToken);
        return result.State switch
        {
            "waiting-file" or "pending" => new ProviderTaskStatus(
                ProviderTaskState.Queued,
                SuggestedPollDelay: TimeSpan.FromSeconds(5)),
            "converting" or "running" => new ProviderTaskStatus(
                ProviderTaskState.Running,
                SuggestedPollDelay: TimeSpan.FromSeconds(5)),
            "done" => new ProviderTaskStatus(ProviderTaskState.Succeeded),
            "failed" => new ProviderTaskStatus(
                ProviderTaskState.Failed,
                "mineru-cloud-task-failed",
                "The MinerU Cloud task failed."),
            _ => new ProviderTaskStatus(
                ProviderTaskState.Unknown,
                "mineru-cloud-task-state-unknown",
                "The MinerU Cloud task returned an unknown state.",
                SuggestedPollDelay: TimeSpan.FromSeconds(5)),
        };
    }

    public async Task<ProviderResultContent> OpenResultAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetBatchResultAsync(
            configuration,
            externalTaskId,
            cancellationToken);
        if (result.State == "failed")
        {
            throw new ProviderException(
                "mineru-cloud-task-failed",
                "The MinerU Cloud task failed.",
                ProviderFailureCategory.Permanent);
        }

        if (result.State != "done")
        {
            throw new ProviderException(
                "mineru-cloud-result-not-ready",
                "The MinerU Cloud result is not ready for download.",
                ProviderFailureCategory.Transient);
        }

        var resultUri = MinerUHttpProtocol.ValidateSignedUri(
            result.FullZipUrl,
            "cloud-result");
        using var request = new HttpRequestMessage(HttpMethod.Get, resultUri);
        var response = await MinerUHttpProtocol.SendAsync(
            signedTransferClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            "cloud-result",
            cancellationToken);
        try
        {
            MinerUHttpProtocol.EnsureSuccess(
                response,
                "cloud-result",
                ProviderFailureCategory.Permanent);
            var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType
                ?? "application/octet-stream";
            return new ProviderResultContent(
                new HttpResponseOwnedStream(content, response),
                mediaType,
                "result.zip");
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public Task TryCancelAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: true);
        RequireCredential(configuration);
        MinerUHttpProtocol.ValidateExternalTaskId(externalTaskId);
        return Task.CompletedTask;
    }

    private async Task UploadSourceAsync(
        ProviderDocumentSource source,
        Uri uploadUri,
        CancellationToken cancellationToken)
    {
        await using var sourceContent = await source.OpenReadAsync(cancellationToken);
        if (!sourceContent.CanRead)
        {
            throw new ProviderException(
                "provider-source-unreadable",
                "The source document stream is not readable.",
                ProviderFailureCategory.Input);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri);
        var content = new StreamContent(sourceContent);
        content.Headers.ContentLength = source.SizeBytes;
        request.Content = content;

        using var response = await MinerUHttpProtocol.SendAsync(
            signedTransferClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            "cloud-upload",
            cancellationToken);
        MinerUHttpProtocol.EnsureSuccess(
            response,
            "cloud-upload",
            ProviderFailureCategory.Permanent);
    }

    private async Task<CloudBatchResult> GetBatchResultAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: true);
        RequireCredential(configuration);
        var taskId = MinerUHttpProtocol.ValidateExternalTaskId(externalTaskId);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            MinerUHttpProtocol.BuildEndpoint(
                configuration.BaseUri,
                $"api/v4/extract-results/batch/{taskId}"));
        MinerUHttpProtocol.AddBearerCredential(
            request,
            configuration.Credential,
            required: true);

        using var response = await MinerUHttpProtocol.SendAsync(
            providerApiClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            "cloud-status",
            cancellationToken);
        MinerUHttpProtocol.EnsureSuccess(response, "cloud-status");
        using var payload = await MinerUHttpProtocol.ReadJsonAsync(
            response,
            "cloud-status",
            cancellationToken);
        var data = ReadSuccessfulData(payload.RootElement, "cloud-status");
        if (!data.TryGetProperty("extract_result", out var extractResults)
            || extractResults.ValueKind != JsonValueKind.Array
            || extractResults.GetArrayLength() != 1
            || extractResults[0].ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse(
                "cloud-status",
                "The MinerU Cloud status response did not contain one file result.");
        }

        var result = extractResults[0];
        return new CloudBatchResult(
            ReadRequiredString(result, "state", "cloud-status"),
            result.TryGetProperty("full_zip_url", out var url)
                && url.ValueKind == JsonValueKind.String
                    ? url.GetString()
                    : null);
    }

    private static Dictionary<string, object> BuildSubmissionPayload(
        ProviderExecutionConfiguration configuration,
        MinerUProviderOptions options,
        string fileName,
        string dataId)
    {
        var file = new Dictionary<string, object>
        {
            ["name"] = fileName,
            ["data_id"] = dataId,
            ["is_ocr"] = options.Ocr ?? false,
        };
        var pageRange = BuildCloudPageRange(options);
        if (pageRange is not null)
        {
            file["page_ranges"] = pageRange;
        }

        var payload = new Dictionary<string, object>
        {
            ["files"] = new[] { file },
            ["model_version"] = configuration.Model ?? "pipeline",
            ["enable_formula"] = options.Formula,
            ["enable_table"] = options.Table,
            ["language"] = options.Language,
        };
        return payload;
    }

    private static string? BuildCloudPageRange(MinerUProviderOptions options)
    {
        if (!options.StartPage.HasValue && !options.EndPage.HasValue)
        {
            return null;
        }

        var start = checked((options.StartPage ?? 0) + 1);
        return options.EndPage.HasValue
            ? $"{start}-{checked(options.EndPage.Value + 1)}"
            : $"{start}--1";
    }

    private static JsonElement ReadSuccessfulData(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("code", out var code)
            || !code.TryGetInt32(out var codeValue)
            || codeValue != 0)
        {
            throw new ProviderException(
                $"mineru-{operation}-rejected",
                $"The MinerU {operation} request was rejected by the Provider.",
                ProviderFailureCategory.Permanent);
        }

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse(
                operation,
                $"The MinerU {operation} response is missing 'data'.");
        }

        return data;
    }

    private static string ReadRequiredString(
        JsonElement root,
        string name,
        string operation)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw InvalidResponse(
                operation,
                $"The MinerU {operation} response is missing '{name}'.");
        }

        return value.GetString()!;
    }

    private static void RequireCredential(ProviderExecutionConfiguration configuration)
    {
        if (configuration.Credential is null)
        {
            throw new ProviderException(
                "provider-credential-required",
                "MinerU Cloud requires a credential.",
                ProviderFailureCategory.Configuration);
        }
    }

    private static ProviderException InvalidResponse(string operation, string message) => new(
        $"mineru-{operation}-response-invalid",
        message,
        ProviderFailureCategory.Permanent);

    private sealed record CloudBatchResult(string State, string? FullZipUrl);
}
