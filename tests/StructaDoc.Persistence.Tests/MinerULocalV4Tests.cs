using System.Net;
using System.Text;
using StructaDoc.Adapters.Providers;
using StructaDoc.Application.Providers;

namespace StructaDoc.Persistence.Tests;

public sealed partial class MinerUParseProviderTests
{
    [Theory]
    [InlineData("flash")]
    [InlineData("basic")]
    [InlineData("standard")]
    [InlineData("advanced")]
    public async Task V4_selects_exact_tier_and_sends_only_one_file_with_query_options(string tier)
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal($"https://local.example/mineru/v1/tasks?tier={tier}&ocr_mode=txt&output_formats=zip&page_range=3-5", request.RequestUri!.AbsoluteUri);
            Assert.Equal("v4-token", request.Headers.Authorization?.Parameter);
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var file = Assert.Single(multipart);
            Assert.Equal("files", file.Headers.ContentDisposition!.Name?.Trim('"'));
            Assert.Equal("application/pdf", file.Headers.ContentType!.MediaType);
            Assert.Equal("source", await file.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.DoesNotContain("v4-token", await multipart.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken), StringComparison.Ordinal);
            return JsonResponse(HttpStatusCode.Accepted, """{"task_id":"task/id?#","status_url":"https://untrusted.invalid/status","result_url":"https://untrusted.invalid/result"}""");
        });
        var provider = new MinerULocalParseProvider(new HttpClient(handler));
        var config = LocalConfiguration(backend: $"v4:{tier}", credential: "v4-token");
        var capabilities = await provider.GetCapabilitiesAsync(config, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(200L * 1024 * 1024, capabilities.MaxFileBytes);
        Assert.Null(capabilities.MaxPages);
        Assert.False(capabilities.SupportsCancellation);
        Assert.Equal((await provider.GetCapabilitiesAsync(LocalConfiguration(), cancellationToken: TestContext.Current.CancellationToken)).SupportedMediaTypes.Order(), capabilities.SupportedMediaTypes.Order());
        Assert.Null(await provider.PrepareSubmissionAsync(config, Guid.NewGuid(), Source("quote \"name.pdf", "source"u8.ToArray()), "{}", cancellationToken: TestContext.Current.CancellationToken));
        var submission = await provider.SubmitAsync(config, Guid.NewGuid(), Source("quote \"name.pdf", "source"u8.ToArray()), """{"ocr":true,"parseMethod":"txt","startPage":2,"endPage":4}""", null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("task/id?#", submission.ExternalTaskId);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("{}", "auto", null)]
    [InlineData("{\"ocr\":false}", "auto", null)]
    [InlineData("{\"ocr\":true}", "ocr", null)]
    [InlineData("{\"parseMethod\":\"ocr\"}", "ocr", null)]
    [InlineData("{\"ocr\":true,\"parseMethod\":\"auto\"}", "auto", null)]
    [InlineData("{\"startPage\":0}", "auto", "1-r1")]
    [InlineData("{\"endPage\":0}", "auto", "1-1")]
    [InlineData("{\"startPage\":2,\"endPage\":2}", "auto", "3-3")]
    [InlineData("{\"startPage\":2147483646}", "auto", "2147483647-r1")]
    public async Task V4_maps_supported_options(string options, string ocr, string? range)
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal($"?tier=flash&ocr_mode={ocr}&output_formats=zip{(range is null ? "" : $"&page_range={range}")}", request.RequestUri!.Query);
            return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, """{"task_id":"one"}"""));
        });
        await new MinerULocalParseProvider(new HttpClient(handler)).SubmitAsync(LocalConfiguration(backend: "v4:flash"), Guid.NewGuid(), Source("one.pdf", [1]), options, null, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("{\"formula\":true}", "application/pdf")]
    [InlineData("{\"formula\":false}", "application/pdf")]
    [InlineData("{\"table\":true}", "application/pdf")]
    [InlineData("{\"language\":\"ch\"}", "application/pdf")]
    [InlineData("{\"effort\":\"medium\"}", "application/pdf")]
    [InlineData("{\"imageAnalysis\":true}", "application/pdf")]
    [InlineData("{\"imageAnalysis\":false}", "application/pdf")]
    [InlineData("{\"startPage\":0}", "image/png")]
    [InlineData("{\"endPage\":1}", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("{\"startPage\":2147483647}", "application/pdf")]
    [InlineData("{\"endPage\":2147483647}", "application/pdf")]
    [InlineData("{\"tier\":\"flash\"}", "application/pdf")]
    [InlineData("{\"ocr\":true,\"ocr\":false}", "application/pdf")]
    [InlineData("{\"ocr\":null}", "application/pdf")]
    [InlineData("{\"startPage\":-1}", "application/pdf")]
    [InlineData("{\"startPage\":2,\"endPage\":1}", "application/pdf")]
    public async Task V4_rejects_unsupported_presence_and_invalid_ranges_before_source_or_http(string options, string mediaType)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP."));
        var source = new ProviderDocumentSource("source", mediaType, 1, _ => throw new InvalidOperationException("No source read."));
        var provider = new MinerULocalParseProvider(new HttpClient(handler));
        var config = LocalConfiguration(backend: "v4:flash");
        foreach (var prepare in new[] { true, false })
        {
            var exception = await Assert.ThrowsAsync<ProviderException>(async () =>
            {
                if (prepare) await provider.PrepareSubmissionAsync(config, Guid.NewGuid(), source, options, cancellationToken: TestContext.Current.CancellationToken);
                else await provider.SubmitAsync(config, Guid.NewGuid(), source, options, null, cancellationToken: TestContext.Current.CancellationToken);
            });
            Assert.Equal("mineru-options-invalid", exception.ErrorCode);
            Assert.Equal(ProviderFailureCategory.Input, exception.Category);
        }
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("v4:")]
    [InlineData("v4:FLASH")]
    [InlineData("v4:flash ")]
    [InlineData("v4:unknown")]
    [InlineData("v4:flash?token=value")]
    public async Task V4_rejects_malformed_reserved_selectors_for_every_operation(string backend)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP."));
        var provider = new MinerULocalParseProvider(new HttpClient(handler));
        var config = LocalConfiguration(backend: backend);
        var source = new ProviderDocumentSource("a.pdf", "application/pdf", 1, _ => throw new InvalidOperationException("No source read."));
        Func<Task>[] operations = [() => provider.GetCapabilitiesAsync(config, cancellationToken: TestContext.Current.CancellationToken), () => provider.PrepareSubmissionAsync(config, Guid.NewGuid(), source, "{}", cancellationToken: TestContext.Current.CancellationToken), () => provider.SubmitAsync(config, Guid.NewGuid(), source, "{}", null, cancellationToken: TestContext.Current.CancellationToken), () => provider.GetStatusAsync(config, "task", cancellationToken: TestContext.Current.CancellationToken), () => provider.OpenResultAsync(config, "task", cancellationToken: TestContext.Current.CancellationToken), () => provider.TryCancelAsync(config, "task", cancellationToken: TestContext.Current.CancellationToken)];
        foreach (var operation in operations)
        {
            Assert.Equal(ProviderFailureCategory.Configuration, (await Assert.ThrowsAsync<ProviderException>(operation)).Category);
        }
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("queued", ProviderTaskState.Queued)]
    [InlineData("running", ProviderTaskState.Running)]
    [InlineData("completed", ProviderTaskState.Succeeded)]
    [InlineData("failed", ProviderTaskState.Failed)]
    [InlineData("canceled", ProviderTaskState.Failed)]
    [InlineData("partial", ProviderTaskState.Failed)]
    [InlineData("future", ProviderTaskState.Unknown)]
    public async Task V4_maps_task_states_and_ignores_returned_urls(string state, ProviderTaskState expected)
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://local.example/mineru/v1/tasks/task%2Fid%3F%23", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, $$"""{"status":"{{state}}","queued_ahead":"malformed","files":[{"status":"completed"}],"status_url":"https://untrusted.invalid/","error":"sensitive-body"}"""));
        });
        var status = await new MinerULocalParseProvider(new HttpClient(handler)).GetStatusAsync(LocalConfiguration(backend: "v4:flash"), "task/id?#", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, status.State);
        Assert.DoesNotContain("sensitive-body", status.ErrorMessage ?? "", StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"files\":[]}")]
    [InlineData("{\"files\":[{\"status\":\"completed\"},{\"status\":\"completed\"}]}")]
    [InlineData("{\"files\":[{\"status\":\"failed\"}]}")]
    [InlineData("{\"files\":[{\"status\":\"partial\"}]}")]
    [InlineData("{\"files\":[{\"status\":\"running\"}]}")]
    [InlineData("{\"files\":[null]}")]
    [InlineData("{\"files\":null}")]
    public async Task V4_completed_requires_exactly_one_completed_file(string suffix)
    {
        var json = "{\"status\":\"completed\"," + suffix[1..];
        if (suffix == "{}") json = "{\"status\":\"completed\"}";
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var status = await new MinerULocalParseProvider(new HttpClient(handler)).GetStatusAsync(LocalConfiguration(backend: "v4:flash"), "task", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ProviderTaskState.Failed, status.State);
        Assert.Equal("mineru-local-task-failed", status.ErrorCode);
    }

    [Theory]
    [InlineData("not-json", "mineru-local-status-response-invalid")]
    [InlineData("[]", "mineru-local-status-response-invalid")]
    [InlineData("{}", "mineru-local-status-response-invalid")]
    [InlineData("{\"status\":0}", "mineru-local-status-response-invalid")]
    public async Task V4_malformed_status_is_a_sanitized_permanent_failure(string json, string code)
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, json)));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new MinerULocalParseProvider(new HttpClient(handler)).GetStatusAsync(LocalConfiguration(backend: "v4:flash"), "task", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(code, exception.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Permanent, exception.Category);
    }

    [Fact]
    public async Task V4_json_size_is_bounded()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new string('s', 1024 * 1024 + 1))));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new MinerULocalParseProvider(new HttpClient(handler)).GetStatusAsync(LocalConfiguration(backend: "v4:flash"), "task", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("mineru-local-status-response-too-large", exception.ErrorCode);
    }

    [Theory]
    [InlineData(202, ProviderFailureCategory.Transient)]
    [InlineData(409, ProviderFailureCategory.Permanent)]
    [InlineData(401, ProviderFailureCategory.Configuration)]
    [InlineData(403, ProviderFailureCategory.Configuration)]
    [InlineData(400, ProviderFailureCategory.Input)]
    [InlineData(422, ProviderFailureCategory.Input)]
    [InlineData(408, ProviderFailureCategory.Transient)]
    [InlineData(429, ProviderFailureCategory.Transient)]
    [InlineData(500, ProviderFailureCategory.Transient)]
    [InlineData(302, ProviderFailureCategory.Permanent)]
    public async Task V4_result_categories_and_owned_failure_disposal(int code, ProviderFailureCategory category)
    {
        var stream = new TrackingStream("sensitive-body"u8.ToArray());
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://local.example/mineru/v1/tasks/task%2Fid/result?response_format=zip", request.RequestUri!.AbsoluteUri);
            Assert.Equal("v4-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StreamContent(stream), Headers = { Location = new Uri("https://untrusted.invalid/") } });
        });
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new MinerULocalParseProvider(new HttpClient(handler)).OpenResultAsync(LocalConfiguration(backend: "v4:basic", credential: "v4-token"), "task/id", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(category, exception.Category);
        Assert.DoesNotContain("sensitive-body", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("v4-token", exception.ToString(), StringComparison.Ordinal);
        Assert.True(stream.IsDisposed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task V4_result_stream_lifetime_and_local_operation_cancellation_are_preserved()
    {
        var stream = new TrackingStream("PK\u0003\u0004zip"u8.ToArray());
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var provider = new MinerULocalParseProvider(new HttpClient(handler));
        var config = LocalConfiguration(backend: "v4:flash");
        await using (var result = await provider.OpenResultAsync(config, "task", cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.False(stream.IsDisposed);
            Assert.Equal('P', result.Content.ReadByte());
        }
        Assert.True(stream.IsDisposed);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetStatusAsync(config, "task", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenResultAsync(config, "task", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SubmitAsync(config, Guid.NewGuid(), Source("one.pdf", [1]), "{}", null, canceled.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pipeline")]
    [InlineData("hybrid-engine")]
    public async Task Concurrent_versions_keep_legacy_paths_tokens_options_and_saved_task_protocol(string? backend)
    {
        var requests = new System.Collections.Concurrent.ConcurrentBag<string>();
        var handler = new StubHandler(async request =>
        {
            var token = request.Headers.Authorization!.Parameter;
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            Assert.Equal(token == "old" ? "/mineru/tasks" : "/mineru/v1/tasks", path);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
            if (token == "old" && backend is not null) AssertMultipartField(body, "backend", backend);
            else Assert.DoesNotContain("name=backend", body, StringComparison.Ordinal);
            return JsonResponse(HttpStatusCode.Accepted, """{"task_id":"saved"}""");
        });
        var provider = new MinerULocalParseProvider(new HttpClient(handler));
        var old = LocalConfiguration(credential: "old", backend: backend);
        var current = LocalConfiguration(credential: "new", backend: "v4:advanced");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => provider.SubmitAsync(i % 2 == 0 ? old : current, Guid.NewGuid(), Source("source.pdf", [1]), "{}", null, cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(8, requests.Count);
        var pollHandler = new StubHandler(request =>
        {
            Assert.Equal("/mineru/tasks/saved", request.RequestUri!.AbsolutePath);
            Assert.Equal("old", request.Headers.Authorization!.Parameter);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"status":"completed"}"""));
        });
        Assert.Equal(ProviderTaskState.Succeeded, (await new MinerULocalParseProvider(new HttpClient(pollHandler)).GetStatusAsync(old, "saved", cancellationToken: TestContext.Current.CancellationToken)).State);
    }
    [Theory]
    [InlineData(200, "{\"task_id\":\"one\"}", "mineru-local-submit-status-invalid", ProviderFailureCategory.Permanent)]
    [InlineData(202, "[]", "mineru-local-submit-response-invalid", ProviderFailureCategory.Permanent)]
    [InlineData(202, "{\"task_id\":\"bad\\nvalue\"}", "provider-external-task-id-invalid", ProviderFailureCategory.Permanent)]
    [InlineData(202, "{\"task_id\":0}", "mineru-local-submit-response-invalid", ProviderFailureCategory.Permanent)]
    [InlineData(401, "sensitive-body", "mineru-local-submit-http-401", ProviderFailureCategory.Configuration)]
    [InlineData(422, "sensitive-body", "mineru-local-submit-http-422", ProviderFailureCategory.Input)]
    [InlineData(503, "sensitive-body", "mineru-local-submit-http-503", ProviderFailureCategory.Transient)]
    public async Task V4_submission_requires_202_and_valid_task_id(int status, string body, string code, ProviderFailureCategory category)
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse((HttpStatusCode)status, body)));
        var error = await Assert.ThrowsAsync<ProviderException>(() => new MinerULocalParseProvider(new HttpClient(handler)).SubmitAsync(LocalConfiguration(backend: "v4:flash", credential: "v4-token"), Guid.NewGuid(), Source("one.pdf", [1]), "{}", null, TestContext.Current.CancellationToken));
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(category, error.Category);
        Assert.DoesNotContain("sensitive-body", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("v4-token", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V4_transport_timeouts_and_network_errors_remain_transient(bool timeout)
    {
        var handler = new StubHandler(_ => timeout ? throw new TaskCanceledException() : throw new HttpRequestException("Synthetic network failure."));
        var error = await Assert.ThrowsAsync<ProviderException>(() => new MinerULocalParseProvider(new HttpClient(handler)).GetStatusAsync(LocalConfiguration(backend: "v4:flash"), "saved", TestContext.Current.CancellationToken));
        Assert.Equal(timeout ? "mineru-local-status-timeout" : "mineru-local-status-network-error", error.ErrorCode);
        Assert.Equal(ProviderFailureCategory.Transient, error.Category);
    }

    [Fact]
    public async Task V4_preserves_source_limits_and_does_not_enable_html()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP."));
        var provider = new MinerULocalParseProvider(new HttpClient(handler));
        foreach (var source in new[]
        {
            new ProviderDocumentSource("large.pdf", "application/pdf", 200L * 1024 * 1024 + 1, _ => throw new InvalidOperationException("No read.")),
            new ProviderDocumentSource("one.html", "text/html", 1, _ => throw new InvalidOperationException("No read.")),
        })
        {
            var error = await Assert.ThrowsAsync<ProviderException>(() => provider.PrepareSubmissionAsync(LocalConfiguration(backend: "v4:flash"), Guid.NewGuid(), source, "{}", TestContext.Current.CancellationToken));
            Assert.Equal(ProviderFailureCategory.Input, error.Category);
        }
        Assert.Equal(0, handler.RequestCount);
    }

}
