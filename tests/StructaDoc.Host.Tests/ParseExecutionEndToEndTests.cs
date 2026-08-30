using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Contracts.Documents;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Contracts.Providers;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.Tests;

/// <summary>
/// The whole parse path, driven the way a deployment drives it: an administrator configures a
/// Provider, a document is uploaded, a Parse Run is created, and the resident execution Worker takes
/// it from there. Nothing is substituted below the public API — the real MinerU Local adapter talks
/// HTTP to <see cref="StubMinerUServer"/> over a real socket, and the real Worker claims, leases,
/// submits, polls, downloads, normalizes, and commits.
///
/// Every other test in this suite stops short of one of those steps. This is the one that fails if
/// they do not fit together.
/// </summary>
public sealed class ParseExecutionEndToEndTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task A_parse_run_reaches_canonical_results_through_the_worker_and_a_real_provider()
    {
        await using var provider = await StubMinerUServer.StartAsync();
        using var application = CreateExecutingHost();
        using var client = application.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createConfig = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "End-to-end MinerU",
                "mineru-local",
                provider.BaseUrl,
                Backend: "pipeline",
                IsDefault: true),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createConfig.StatusCode);

        var document = await UploadDocumentAsync(client);
        using var createParse = await client.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/parse-runs",
            new ParseRunCreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);
        var parseRun = await createParse.Content.ReadFromJsonAsync<ParseRunResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createParse.StatusCode);
        Assert.Equal("queued", parseRun!.Status);

        // Nothing else pushes it along: the resident Worker has to notice the queued run, claim it,
        // and carry it to a final status on its own.
        var succeeded = await WaitForFinalStatusAsync(client, parseRun.Id);
        Assert.Equal("succeeded", succeeded.Status);
        Assert.Null(succeeded.ErrorCode);
        Assert.NotNull(succeeded.CompletedAt);

        // What the adapter actually put on the wire, rather than what it was asked to.
        Assert.Equal(1, provider.SubmitRequests);
        Assert.True(provider.StatusRequests >= 2, "The Worker should have polled past 'processing'.");
        Assert.Equal(1, provider.ResultRequests);
        Assert.Equal("parse-run.pdf", provider.ReceivedFileName);
        Assert.Equal("application/pdf", provider.ReceivedContentType);
        Assert.Equal(document.SizeBytes, provider.ReceivedFileBytes);
        Assert.Equal("pipeline", provider.ReceivedBackend);

        var blocks = await client.GetFromJsonAsync<ParseBlockListResponse>(
            $"/api/v1/parse-runs/{parseRun.Id:D}/blocks?limit=100",
            cancellationToken: TestContext.Current.CancellationToken);
        var pages = await client.GetFromJsonAsync<ParsePageResponse[]>(
            $"/api/v1/parse-runs/{parseRun.Id:D}/pages",
            cancellationToken: TestContext.Current.CancellationToken);
        var assets = await client.GetFromJsonAsync<ParseAssetResponse[]>(
            $"/api/v1/parse-runs/{parseRun.Id:D}/assets",
            cancellationToken: TestContext.Current.CancellationToken);
        var artifacts = await client.GetFromJsonAsync<ParseArtifactResponse[]>(
            $"/api/v1/parse-runs/{parseRun.Id:D}/artifacts",
            cancellationToken: TestContext.Current.CancellationToken);

        // The stub's content list, having survived download, normalization, and the commit.
        Assert.Equal(3, blocks!.Items.Count);
        Assert.Equal("title", blocks.Items[0].Type);
        Assert.Equal(StubMinerUServer.MarkdownHeading, blocks.Items[0].Content);
        Assert.Equal(1, blocks.Items[0].PageNumber);
        Assert.Equal(StubMinerUServer.MarkdownBody, blocks.Items[1].Content);
        Assert.Equal(2, blocks.Items[2].PageNumber);
        // The image Block resolves to a stored Asset rather than to a Provider path.
        Assert.NotNull(blocks.Items[2].AssetId);
        Assert.Equal(2, pages!.Length);
        var asset = Assert.Single(assets!);
        Assert.Equal("image/png", asset.MediaType);
        Assert.Equal(asset.Id, blocks.Items[2].AssetId);
        Assert.Contains(artifacts!, artifact => artifact.Type == "markdown");
        Assert.Contains(artifacts!, artifact => artifact.Type == "content-list");
        Assert.Contains(artifacts!, artifact => artifact.Type == "provider-archive");

        using var markdown = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRun.Id:D}/markdown",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
        Assert.Contains(
            StubMinerUServer.MarkdownBody,
            await markdown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        // Markdown a Provider produced is not something a person can read as Markdown, and the
        // workspace shows this rendering rather than the source. It is the last step of the chain,
        // so it is checked on the result the chain actually produced rather than on a fixture.
        using var preview = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRun.Id:D}/markdown/preview",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("text/html", preview.Content.Headers.ContentType?.MediaType);
        var previewHtml = await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Anchored on the closing tag rather than the opening one: the renderer gives headings an
        // identifier, so the opening tag carries an attribute this assertion has no stake in.
        Assert.Contains($">{StubMinerUServer.MarkdownHeading}</h1>", previewHtml, StringComparison.Ordinal);
        Assert.Contains(StubMinerUServer.MarkdownBody, previewHtml, StringComparison.Ordinal);

        // The Asset is downloadable, which is the only proof that the bytes were stored and not
        // merely counted.
        using var assetContent = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRun.Id:D}/assets/{asset.Id:D}/content",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, assetContent.StatusCode);
        Assert.Equal(asset.SizeBytes, (await assetContent.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken)).Length);

        var documentRuns = await client.GetFromJsonAsync<ParseRunResponse[]>(
            $"/api/v1/documents/{document.Id:D}/parse-runs",
            cancellationToken: TestContext.Current.CancellationToken);
        var listed = Assert.Single(documentRuns!);
        Assert.Equal(parseRun.Id, listed.Id);
        Assert.Equal("succeeded", listed.Status);

        using var export = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRun.Id:D}/exports/zip",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var exported = new ZipArchive(
            new MemoryStream(await export.Content.ReadAsByteArrayAsync(
                TestContext.Current.CancellationToken)),
            ZipArchiveMode.Read);
        Assert.NotEmpty(exported.Entries);
    }

    [Fact]
    public async Task A_provider_that_rejects_the_submission_leaves_a_failed_run_and_a_reusable_document()
    {
        await using var provider = await StubMinerUServer.StartAsync(rejectSubmission: true);
        using var application = CreateExecutingHost();
        using var client = application.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var createConfig = await client.PostAsJsonAsync(
            "/api/v1/admin/provider-configs",
            new ProviderConfigRequest(
                "Rejecting MinerU",
                "mineru-local",
                provider.BaseUrl,
                IsDefault: true),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createConfig.StatusCode);

        var document = await UploadDocumentAsync(client);
        using var createParse = await client.PostAsJsonAsync(
            $"/api/v1/documents/{document.Id:D}/parse-runs",
            new ParseRunCreateRequest(MaxAttempts: 1),
            cancellationToken: TestContext.Current.CancellationToken);
        var parseRun = await createParse.Content.ReadFromJsonAsync<ParseRunResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createParse.StatusCode);

        // A Provider that answers 400 is a permanent input failure, so the run ends rather than
        // retrying against an answer that will not change.
        var failed = await WaitForFinalStatusAsync(client, parseRun!.Id);
        Assert.Equal("failed", failed.Status);
        Assert.NotNull(failed.ErrorCode);
        Assert.DoesNotContain("stack", failed.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // The Document is untouched by the failure and can be parsed again once the Provider is
        // fixed, which is what makes a failed attempt recoverable from the browser.
        using var reread = await client.GetAsync(
            $"/api/v1/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
    }

    /// <summary>
    /// The shared test host leaves the execution worker out so that Parse Runs written as fixtures
    /// stay where a test put them; this is one of the few places execution is the subject, so it is
    /// added back. The poll delays are shortened because the Provider answers instantly and the
    /// default second-long waits would otherwise dominate the test.
    /// </summary>
    private WebApplicationFactory<Program> CreateExecutingHost()
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
                services.AddHostedService<ParseRunExecutionWorker>());
            builder.UseSetting("Worker:MaintenanceInterval", "00:00:00.100");
            builder.UseSetting("Worker:MinimumPollDelay", "00:00:00.100");
            builder.UseSetting("Worker:MaximumPollDelay", "00:00:00.200");
            builder.UseSetting("Worker:MaxExecutionDuration", "00:01:00");
        });
    }

    private static async Task<ParseRunResponse> WaitForFinalStatusAsync(
        HttpClient client,
        Guid parseRunId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        ParseRunResponse? parseRun = null;

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/v1/parse-runs/{parseRunId:D}");
            response.EnsureSuccessStatusCode();
            parseRun = await response.Content.ReadFromJsonAsync<ParseRunResponse>();
            if (parseRun?.Status is "succeeded" or "failed" or "cancelled")
            {
                return parseRun;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"Parse Run '{parseRunId:D}' did not finish; last status was '{parseRun?.Status}' at stage '{parseRun?.Stage}' with error '{parseRun?.ErrorCode}'.");
    }

    private static async Task<DocumentResponse> UploadDocumentAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(
            "%PDF-1.7\nStructaDoc end-to-end execution sample\n%%EOF"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(content, "file", "parse-run.pdf");
        using var response = await client.PostAsync("/api/v1/documents", multipart);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DocumentResponse>()
            ?? throw new InvalidOperationException("Upload returned no Document.");
    }
}
