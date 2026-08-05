using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Infrastructure.Providers;

public sealed class MinerULocalParseProvider(HttpClient httpClient) : IParseProvider
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
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ],
        maxFileBytes: null,
        maxPages: null,
        supportsCancellation: false);

    public string ProviderType => ProviderTypes.MinerULocal;

    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        ProviderExecutionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: false);
        return Task.FromResult(Capabilities);
    }

    public Task<ProviderSubmissionCheckpoint?> PrepareSubmissionAsync(
        ProviderExecutionConfiguration configuration,
        Guid parseRunId,
        ProviderDocumentSource source,
        string optionsJson,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: false);
        return Task.FromResult<ProviderSubmissionCheckpoint?>(null);
    }

    public async Task<ProviderSubmission> SubmitAsync(
        ProviderExecutionConfiguration configuration,
        Guid parseRunId,
        ProviderDocumentSource source,
        string optionsJson,
        ProviderSubmissionCheckpoint? checkpoint,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: false);
        if (checkpoint is not null)
        {
            throw new ProviderException(
                "mineru-local-checkpoint-unexpected",
                "MinerU Local does not accept a submission checkpoint.",
                ProviderFailureCategory.Permanent);
        }

        if (parseRunId == Guid.Empty)
        {
            throw new ArgumentException("A Parse Run ID is required.", nameof(parseRunId));
        }

        ArgumentNullException.ThrowIfNull(source);
        MinerUHttpProtocol.ValidateSource(source, Capabilities);
        var fileName = MinerUHttpProtocol.ValidateFileName(source.FileName);
        var providerOptions = MinerUProviderOptions.Parse(optionsJson);

        await using var sourceContent = await source.OpenReadAsync(cancellationToken);
        if (!sourceContent.CanRead)
        {
            throw new ProviderException(
                "provider-source-unreadable",
                "The source document stream is not readable.",
                ProviderFailureCategory.Input);
        }

        using var multipart = new MultipartFormDataContent();
        var fileContent = new StreamContent(sourceContent);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(source.MediaType);
        fileContent.Headers.ContentLength = source.SizeBytes;
        multipart.Add(fileContent, "files", fileName);
        AddFormFields(multipart, configuration, providerOptions);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            MinerUHttpProtocol.BuildEndpoint(configuration.BaseUri, "tasks"))
        {
            Content = multipart,
        };
        MinerUHttpProtocol.AddBearerCredential(
            request,
            configuration.Credential,
            required: false);

        using var response = await MinerUHttpProtocol.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            "local-submit",
            cancellationToken);
        MinerUHttpProtocol.EnsureSuccess(response, "local-submit");
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new ProviderException(
                "mineru-local-submit-status-invalid",
                "The MinerU Local submission response did not return HTTP 202.",
                ProviderFailureCategory.Permanent);
        }

        using var payload = await MinerUHttpProtocol.ReadJsonAsync(
            response,
            "local-submit",
            cancellationToken);
        var taskId = ReadRequiredString(payload.RootElement, "task_id", "local-submit");
        MinerUHttpProtocol.ValidateExternalTaskId(taskId);
        return new ProviderSubmission(taskId, ReadSuggestedDelay(payload.RootElement));
    }

    public async Task<ProviderTaskStatus> GetStatusAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: false);
        var taskId = MinerUHttpProtocol.ValidateExternalTaskId(externalTaskId);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            MinerUHttpProtocol.BuildEndpoint(configuration.BaseUri, $"tasks/{taskId}"));
        MinerUHttpProtocol.AddBearerCredential(
            request,
            configuration.Credential,
            required: false);

        using var response = await MinerUHttpProtocol.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            "local-status",
            cancellationToken);
        MinerUHttpProtocol.EnsureSuccess(response, "local-status");
        using var payload = await MinerUHttpProtocol.ReadJsonAsync(
            response,
            "local-status",
            cancellationToken);

        var status = ReadRequiredString(payload.RootElement, "status", "local-status");
        var suggestedDelay = ReadSuggestedDelay(payload.RootElement);
        return status switch
        {
            "pending" => new ProviderTaskStatus(
                ProviderTaskState.Queued,
                SuggestedPollDelay: suggestedDelay),
            "processing" => new ProviderTaskStatus(
                ProviderTaskState.Running,
                SuggestedPollDelay: suggestedDelay),
            "completed" => new ProviderTaskStatus(ProviderTaskState.Succeeded),
            "failed" => new ProviderTaskStatus(
                ProviderTaskState.Failed,
                "mineru-local-task-failed",
                "The MinerU Local task failed."),
            _ => new ProviderTaskStatus(
                ProviderTaskState.Unknown,
                "mineru-local-task-state-unknown",
                "The MinerU Local task returned an unknown state.",
                SuggestedPollDelay: suggestedDelay),
        };
    }

    public async Task<ProviderResultContent> OpenResultAsync(
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: false);
        var taskId = MinerUHttpProtocol.ValidateExternalTaskId(externalTaskId);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            MinerUHttpProtocol.BuildEndpoint(
                configuration.BaseUri,
                $"tasks/{taskId}/result"));
        MinerUHttpProtocol.AddBearerCredential(
            request,
            configuration.Credential,
            required: false);

        var response = await MinerUHttpProtocol.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            "local-result",
            cancellationToken);
        try
        {
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                throw new ProviderException(
                    "mineru-local-result-not-ready",
                    "The MinerU Local result is not ready for download.",
                    ProviderFailureCategory.Transient);
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new ProviderException(
                    "mineru-local-task-failed",
                    "The MinerU Local task failed.",
                    ProviderFailureCategory.Permanent);
            }

            MinerUHttpProtocol.EnsureSuccess(response, "local-result");
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new ProviderException(
                    "mineru-local-result-status-invalid",
                    "The MinerU Local result response did not return HTTP 200.",
                    ProviderFailureCategory.Permanent);
            }

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
        MinerUHttpProtocol.ValidateConfiguration(configuration, ProviderType, requireHttps: false);
        MinerUHttpProtocol.ValidateExternalTaskId(externalTaskId);
        return Task.CompletedTask;
    }

    private static void AddFormFields(
        MultipartFormDataContent multipart,
        ProviderExecutionConfiguration configuration,
        MinerUProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(configuration.Backend))
        {
            AddString(multipart, "backend", configuration.Backend);
        }

        AddString(multipart, "lang_list", options.Language);
        AddString(multipart, "parse_method", options.ParseMethod ?? (options.Ocr == true ? "ocr" : "auto"));
        AddString(multipart, "effort", options.Effort ?? "medium");
        AddString(multipart, "formula_enable", options.Formula);
        AddString(multipart, "table_enable", options.Table);
        AddString(multipart, "image_analysis", options.ImageAnalysis ?? true);
        AddString(multipart, "return_md", true);
        AddString(multipart, "return_middle_json", true);
        AddString(multipart, "return_model_output", true);
        AddString(multipart, "return_content_list", true);
        AddString(multipart, "return_images", true);
        AddString(multipart, "response_format_zip", true);
        AddString(multipart, "return_original_file", false);
        AddString(multipart, "client_side_output_generation", false);
        AddString(multipart, "start_page_id", options.StartPage ?? 0);
        AddString(multipart, "end_page_id", options.EndPage ?? 99999);
    }

    private static void AddString(MultipartFormDataContent multipart, string name, object value) =>
        multipart.Add(
            new StringContent(Convert.ToString(value, CultureInfo.InvariantCulture)!.ToLowerInvariant()),
            name);

    private static string ReadRequiredString(
        JsonElement root,
        string name,
        string operation)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ProviderException(
                $"mineru-{operation}-response-invalid",
                $"The MinerU {operation} response is missing '{name}'.",
                ProviderFailureCategory.Permanent);
        }

        return value.GetString()!;
    }

    private static TimeSpan ReadSuggestedDelay(JsonElement root)
    {
        if (root.TryGetProperty("queued_ahead", out var queuedAhead)
            && queuedAhead.TryGetInt32(out var queuedCount)
            && queuedCount > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(30, 2 + queuedCount));
        }

        return TimeSpan.FromSeconds(2);
    }
}
