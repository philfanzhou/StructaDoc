using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Providers;
using StructaDoc.Adapters.Providers;

namespace StructaDoc.Persistence.Tests;

public sealed class MinerUParseProviderTests
{
    [Fact]
    public async Task Local_submit_streams_multipart_options_and_reads_task_id()
    {
        string? requestBody = null;
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://local.example/mineru/tasks", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("local-credential", request.Headers.Authorization?.Parameter);
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(
                HttpStatusCode.Accepted,
                """{"task_id":"local-task-1","queued_ahead":3}""");
        });
        var provider = new MinerULocalParseProvider(new HttpClient(handler));

        var submission = await provider.SubmitAsync(
            LocalConfiguration(credential: "local-credential", backend: "hybrid-engine"),
            Guid.NewGuid(),
            Source("sample.pdf", "document"u8.ToArray()),
            """{"ocr":true,"formula":false,"language":"en","effort":"high"}""",
            checkpoint: null);

        Assert.Equal("local-task-1", submission.ExternalTaskId);
        Assert.Equal(TimeSpan.FromSeconds(5), submission.SuggestedPollDelay);
        Assert.NotNull(requestBody);
        Assert.Contains("name=files", requestBody, StringComparison.Ordinal);
        Assert.Contains("filename=sample.pdf", requestBody, StringComparison.Ordinal);
        Assert.Contains("document", requestBody, StringComparison.Ordinal);
        AssertMultipartField(requestBody, "backend", "hybrid-engine");
        AssertMultipartField(requestBody, "parse_method", "ocr");
        AssertMultipartField(requestBody, "formula_enable", "false");
        AssertMultipartField(requestBody, "return_content_list", "true");
        AssertMultipartField(requestBody, "response_format_zip", "true");
    }

    [Theory]
    [InlineData("pending", ProviderTaskState.Queued)]
    [InlineData("processing", ProviderTaskState.Running)]
    [InlineData("completed", ProviderTaskState.Succeeded)]
    [InlineData("failed", ProviderTaskState.Failed)]
    [InlineData("future-state", ProviderTaskState.Unknown)]
    public async Task Local_status_maps_protocol_states(
        string state,
        ProviderTaskState expectedState)
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://local.example/tasks/task%2Fid", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                $"{{\"status\":\"{state}\"}}"));
        });
        var provider = new MinerULocalParseProvider(new HttpClient(handler));

        var status = await provider.GetStatusAsync(
            LocalConfiguration(baseUri: "https://local.example/"),
            "task/id");

        Assert.Equal(expectedState, status.State);
        Assert.Equal(
            expectedState is ProviderTaskState.Succeeded or ProviderTaskState.Failed
                ? null
                : TimeSpan.FromSeconds(2),
            status.SuggestedPollDelay);
    }

    [Fact]
    public async Task Local_result_keeps_http_response_alive_until_result_is_disposed()
    {
        var stream = new TrackingStream("PK\u0003\u0004result"u8.ToArray());
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://local.example/tasks/task-1/result", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new("application/zip") },
                },
            });
        });
        var provider = new MinerULocalParseProvider(new HttpClient(handler));

        var result = await provider.OpenResultAsync(
            LocalConfiguration(baseUri: "https://local.example/"),
            "task-1");
        Assert.False(stream.IsDisposed);
        Assert.Equal("application/zip", result.MediaType);

        await result.DisposeAsync();

        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task Local_result_reports_pending_response_as_transient()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.Accepted,
            "{\"status\":\"processing\"}")));
        var provider = new MinerULocalParseProvider(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.OpenResultAsync(
                LocalConfiguration(baseUri: "https://local.example/"),
                "task-1"));

        Assert.Equal("mineru-local-result-not-ready", exception.ErrorCode);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task Cloud_submit_allocates_signed_url_then_uploads_without_bearer_token()
    {
        var parseRunId = Guid.NewGuid();
        var requests = 0;
        var handler = new StubHandler(async request =>
        {
            requests++;
            if (requests == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://mineru.example/root/api/v4/file-urls/batch",
                    request.RequestUri!.AbsoluteUri);
                Assert.Equal("cloud-secret", request.Headers.Authorization?.Parameter);
                using var payload = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync());
                Assert.Equal("report.pdf", payload.RootElement
                    .GetProperty("files")[0]
                    .GetProperty("name")
                    .GetString());
                Assert.Equal(parseRunId.ToString("N"), payload.RootElement
                    .GetProperty("files")[0]
                    .GetProperty("data_id")
                    .GetString());
                Assert.Equal("vlm", payload.RootElement.GetProperty("model_version").GetString());
                Assert.Equal("2-4", payload.RootElement
                    .GetProperty("files")[0]
                    .GetProperty("page_ranges")
                    .GetString());
                return JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"code":0,"data":{"batch_id":"batch-1","file_urls":["https://upload.example/signed?key=value"]}}
                    """);
            }

            if (requests == 2)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("cloud-secret", request.Headers.Authorization?.Parameter);
                return JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"code":0,"data":{"batch_id":"batch-1","extract_result":[{"state":"waiting-file"}]}}
                    """);
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(
                "https://upload.example/signed?key=value",
                request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Content!.Headers.ContentType);
            Assert.Equal(8, request.Content.Headers.ContentLength);
            Assert.Equal("document", await request.Content.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = CloudProvider(handler);

        var configuration = CloudConfiguration(credential: "cloud-secret", model: "vlm");
        var source = Source("report.pdf", "document"u8.ToArray());
        var optionsJson = """{"ocr":true,"startPage":1,"endPage":3}""";
        var checkpoint = await provider.PrepareSubmissionAsync(
            configuration,
            parseRunId,
            source,
            optionsJson);
        Assert.NotNull(checkpoint);

        var submission = await provider.SubmitAsync(
            configuration,
            parseRunId,
            source,
            optionsJson,
            checkpoint);

        Assert.Equal("batch-1", submission.ExternalTaskId);
        Assert.Equal(3, requests);
    }

    // A rejection is the Provider's answer and the service cannot reconstruct it. An expired token,
    // an unverified account and an exhausted quota all arrive as HTTP 200 with a non-zero code, so a
    // failure that dropped that code left an administrator with nothing to act on.
    [Fact]
    public async Task Cloud_submit_rejection_carries_the_reason_the_provider_gave()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            """
            {"code":"A0211","msg":"token error","trace_id":"7f3c9a","data":null}
            """)));

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            CloudProvider(handler).PrepareSubmissionAsync(
                CloudConfiguration(credential: "cloud-secret"),
                Guid.NewGuid(),
                Source("report.pdf", "document"u8.ToArray()),
                "{}"));

        Assert.Equal("mineru-cloud-submit-rejected", exception.ErrorCode);
        Assert.Contains("code=A0211", exception.Message);
        Assert.Contains("msg=token error", exception.Message);
        Assert.Contains("trace_id=7f3c9a", exception.Message);
    }

    // The same endpoint answers a successful submission with the presigned upload URL. Describing a
    // rejection by echoing whatever the body held would put that URL into a stored error, a log line
    // and a browser the first time an upstream failure arrived shaped like a success.
    [Fact]
    public async Task Cloud_submit_rejection_does_not_repeat_anything_but_the_reason()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            """
            {"code":-60012,"msg":"quota exceeded","data":{"file_urls":["https://upload.example/signed?key=secret-value"]}}
            """)));

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            CloudProvider(handler).PrepareSubmissionAsync(
                CloudConfiguration(credential: "cloud-secret"),
                Guid.NewGuid(),
                Source("report.pdf", "document"u8.ToArray()),
                "{}"));

        Assert.Contains("code=-60012", exception.Message);
        Assert.Contains("msg=quota exceeded", exception.Message);
        Assert.DoesNotContain("secret-value", exception.Message);
        Assert.DoesNotContain("upload.example", exception.Message);
        Assert.DoesNotContain("cloud-secret", exception.Message);
    }

    // The Provider's gateway spells the same three fields differently from its extraction API. Reading
    // one spelling only is how a real rejection arrived with its trace identifier missing, which is
    // the field its support asks for first.
    [Fact]
    public async Task Cloud_submit_rejection_reads_the_gateway_spelling_of_the_reason()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            """
            {"traceId":"1b24375ed1e7","msgCode":"A0202","msg":"user authenticate failed","success":false}
            """)));

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            CloudProvider(handler).PrepareSubmissionAsync(
                CloudConfiguration(credential: "cloud-secret"),
                Guid.NewGuid(),
                Source("report.pdf", "document"u8.ToArray()),
                "{}"));

        Assert.Equal("mineru-cloud-submit-rejected", exception.ErrorCode);
        Assert.Contains("msgCode=A0202", exception.Message);
        Assert.Contains("traceId=1b24375ed1e7", exception.Message);
    }

    // A body HttpClient cannot measure is sent chunked. These are a few hundred bytes, and a gateway
    // that declines to read a chunked request body hands its handler an empty one -- which comes back
    // as a complaint about the first required field and looks nothing like a framing problem.
    [Fact]
    public async Task Cloud_submit_states_the_length_and_type_of_its_request_body()
    {
        long? sentLength = null;
        string? sentContentType = null;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                sentLength = request.Content!.Headers.ContentLength;
                sentContentType = request.Content.Headers.ContentType?.ToString();
            }

            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                """
                {"code":0,"data":{"batch_id":"batch-1","file_urls":["https://upload.example/signed?key=value"]}}
                """));
        });

        await CloudProvider(handler).PrepareSubmissionAsync(
            CloudConfiguration(credential: "cloud-secret"),
            Guid.NewGuid(),
            Source("report.pdf", "document"u8.ToArray()),
            "{}");

        Assert.NotNull(sentLength);
        Assert.True(sentLength > 0);
        Assert.Equal("application/json", sentContentType);
    }

    // The administration page tells an administrator what a blank Model field will send, and it reads
    // that from the descriptor rather than from this request. Nothing else ties the two together, so
    // an adapter that changed its fallback would leave the form confidently naming the old one.
    [Fact]
    public async Task Cloud_submit_sends_the_model_the_descriptor_advertises_when_none_is_configured()
    {
        string? sentModel = null;
        var handler = new StubHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sentModel = payload.RootElement.GetProperty("model_version").GetString();
            return JsonResponse(
                HttpStatusCode.OK,
                """
                {"code":0,"data":{"batch_id":"batch-1","file_urls":["https://upload.example/signed?key=value"]}}
                """);
        });

        await CloudProvider(handler).PrepareSubmissionAsync(
            CloudConfiguration(credential: "cloud-secret", model: null),
            Guid.NewGuid(),
            Source("report.pdf", "document"u8.ToArray()),
            "{}");

        Assert.Equal(ProviderTypeDescriptors.MinerUCloudDefaultModel, sentModel);
        Assert.Equal("pipeline", sentModel);
    }

    [Fact]
    public async Task Cloud_submit_reuses_checkpoint_and_skips_upload_when_batch_started()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("cloud-secret", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                """
                {"code":0,"data":{"batch_id":"batch-1","extract_result":[{"state":"running"}]}}
                """));
        });
        var provider = CloudProvider(handler);
        var checkpoint = new ProviderSubmissionCheckpoint(
            "batch-1",
            "https://upload.example/signed?key=value");

        var submission = await provider.SubmitAsync(
            CloudConfiguration(credential: "cloud-secret"),
            Guid.NewGuid(),
            Source("report.pdf", "document"u8.ToArray()),
            "{}",
            checkpoint);

        Assert.Equal("batch-1", submission.ExternalTaskId);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Cloud_submit_requires_a_durable_checkpoint_before_http()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException(
            "No HTTP request should be sent."));
        var provider = CloudProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.SubmitAsync(
                CloudConfiguration(credential: "cloud-secret"),
                Guid.NewGuid(),
                Source("report.pdf", "document"u8.ToArray()),
                "{}",
                checkpoint: null));

        Assert.Equal("mineru-cloud-checkpoint-required", exception.ErrorCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("waiting-file", ProviderTaskState.Queued)]
    [InlineData("pending", ProviderTaskState.Queued)]
    [InlineData("converting", ProviderTaskState.Running)]
    [InlineData("running", ProviderTaskState.Running)]
    [InlineData("done", ProviderTaskState.Succeeded)]
    [InlineData("failed", ProviderTaskState.Failed)]
    public async Task Cloud_status_maps_batch_file_state(
        string state,
        ProviderTaskState expectedState)
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("cloud-secret", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                $"{{\"code\":0,\"data\":{{\"batch_id\":\"batch-1\",\"extract_result\":[{{\"state\":\"{state}\"}}]}}}}"));
        });
        var provider = CloudProvider(handler);

        var status = await provider.GetStatusAsync(
            CloudConfiguration(credential: "cloud-secret"),
            "batch-1");

        Assert.Equal(expectedState, status.State);
    }

    [Fact]
    public async Task Cloud_result_download_does_not_forward_provider_credential()
    {
        var calls = 0;
        var resultStream = new TrackingStream("PK\u0003\u0004cloud"u8.ToArray());
        var handler = new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("cloud-secret", request.Headers.Authorization?.Parameter);
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"code":0,"data":{"batch_id":"batch-1","extract_result":[{"state":"done","full_zip_url":"https://cdn.example/result.zip?signature=x"}]}}
                    """));
            }

            Assert.Equal("https://cdn.example/result.zip?signature=x", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(resultStream),
            });
        });
        var provider = CloudProvider(handler);

        var result = await provider.OpenResultAsync(
            CloudConfiguration(credential: "cloud-secret"),
            "batch-1");
        Assert.False(resultStream.IsDisposed);

        await result.DisposeAsync();

        Assert.True(resultStream.IsDisposed);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Provider_http_failures_are_classified_without_response_or_credentials()
    {
        const string responseSecret = "upstream-private-details";
        const string credential = "cloud-private-token";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(responseSecret),
        }));
        var provider = CloudProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.GetStatusAsync(
                CloudConfiguration(credential: credential),
                "batch-1"));

        Assert.True(exception.Retryable);
        Assert.Equal("mineru-cloud-status-http-429", exception.ErrorCode);
        Assert.DoesNotContain(responseSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(credential, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"ocr\":\"yes\"}")]
    [InlineData("{\"ocr\":true,\"ocr\":false}")]
    [InlineData("{\"startPage\":4,\"endPage\":2}")]
    public async Task Provider_rejects_invalid_options_before_sending(string optionsJson)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException(
            "No HTTP request should be sent."));
        var provider = new MinerULocalParseProvider(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.SubmitAsync(
                LocalConfiguration(),
                Guid.NewGuid(),
                Source("sample.pdf", "document"u8.ToArray()),
                optionsJson,
                checkpoint: null));

        Assert.Equal(ProviderFailureCategory.Input, exception.Category);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Cloud_rejects_unsupported_or_oversized_sources_before_sending()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException(
            "No HTTP request should be sent."));
        var provider = CloudProvider(handler);
        var configuration = CloudConfiguration(credential: "cloud-secret");
        var unsupported = new ProviderDocumentSource(
            "book.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            10,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[10])));
        var oversized = new ProviderDocumentSource(
            "large.pdf",
            "application/pdf",
            (200L * 1024 * 1024) + 1,
            _ => Task.FromResult<Stream>(new MemoryStream([1])));

        var unsupportedException = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.PrepareSubmissionAsync(
                configuration,
                Guid.NewGuid(),
                unsupported,
                "{}"));
        var oversizedException = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.PrepareSubmissionAsync(
                configuration,
                Guid.NewGuid(),
                oversized,
                "{}"));

        Assert.Equal("provider-source-media-type-unsupported", unsupportedException.ErrorCode);
        Assert.Equal("provider-source-file-too-large", oversizedException.ErrorCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Cloud_rejects_local_only_options_before_sending()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException(
            "No HTTP request should be sent."));
        var provider = CloudProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.PrepareSubmissionAsync(
                CloudConfiguration(credential: "cloud-secret"),
                Guid.NewGuid(),
                Source("sample.pdf", "document"u8.ToArray()),
                "{\"parseMethod\":\"ocr\"}"));

        Assert.Equal("mineru-options-invalid", exception.ErrorCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void Registration_exposes_one_adapter_per_provider_type()
    {
        var services = new ServiceCollection();
        services.AddStructaDocParseProviders();
        using var serviceProvider = services.BuildServiceProvider();

        var providers = serviceProvider.GetServices<IParseProvider>().ToArray();

        Assert.Equal(2, providers.Length);
        Assert.Contains(providers, provider => provider.ProviderType == ProviderTypes.MinerUCloud);
        Assert.Contains(providers, provider => provider.ProviderType == ProviderTypes.MinerULocal);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:4860:4860::8888", true)]
    public void Signed_transfer_policy_only_allows_public_addresses(
        string address,
        bool expected)
    {
        Assert.Equal(
            expected,
            SignedTransferDestinationPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Signed_transfer_url_rejects_non_standard_https_ports()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            """
            {"code":0,"data":{"batch_id":"batch-1","file_urls":["https://upload.example:8443/signed"]}}
            """)));
        var provider = CloudProvider(handler);
        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.PrepareSubmissionAsync(
                CloudConfiguration(credential: "cloud-secret"),
                Guid.NewGuid(),
                Source("report.pdf", "document"u8.ToArray()),
                "{}"));

        Assert.Equal(ProviderFailureCategory.Security, exception.Category);
    }

    [Fact]
    public async Task Signed_transfer_connection_rejects_private_addresses_as_security_failures()
    {
        var apiHandler = new StubHandler(_ => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            """
            {"code":0,"data":{"batch_id":"batch-1","extract_result":[{"state":"waiting-file"}]}}
            """)));
        using var signedHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = SignedTransferDestinationPolicy.ConnectAsync,
        };
        using var signedClient = new HttpClient(signedHandler);
        var provider = new MinerUCloudParseProvider(
            new HttpClient(apiHandler),
            signedClient);
        var checkpoint = new ProviderSubmissionCheckpoint(
            "batch-1",
            "https://127.0.0.1/upload");

        var exception = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.SubmitAsync(
                CloudConfiguration(credential: "cloud-secret"),
                Guid.NewGuid(),
                Source("report.pdf", "document"u8.ToArray()),
                "{}",
                checkpoint));

        Assert.Equal("mineru-cloud-upload-destination-denied", exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Security, exception.Category);
    }

    private static ProviderExecutionConfiguration LocalConfiguration(
        string baseUri = "https://local.example/mineru/",
        string? credential = null,
        string? backend = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ProviderTypes.MinerULocal,
        new Uri(baseUri),
        model: null,
        backend,
        credential is null ? null : new ProviderCredential(credential));

    private static ProviderExecutionConfiguration CloudConfiguration(
        string? credential,
        string? model = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ProviderTypes.MinerUCloud,
        new Uri("https://mineru.example/root/"),
        model,
        backend: null,
        credential is null ? null : new ProviderCredential(credential));

    private static ProviderDocumentSource Source(string fileName, byte[] content) => new(
        fileName,
        "application/pdf",
        content.Length,
        _ => Task.FromResult<Stream>(new MemoryStream(content, writable: false)));

    private static MinerUCloudParseProvider CloudProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new MinerUCloudParseProvider(client, client);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(
        statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static void AssertMultipartField(string body, string name, string value)
    {
        var nameIndex = body.IndexOf($"name={name}", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Multipart field '{name}' was not found.");
        Assert.Contains(value, body[nameIndex..], StringComparison.Ordinal);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return responseFactory(request);
        }
    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return base.DisposeAsync();
        }
    }
}
