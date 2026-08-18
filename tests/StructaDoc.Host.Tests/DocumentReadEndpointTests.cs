using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Documents;

namespace StructaDoc.Host.Tests;

public sealed class DocumentReadEndpointTests
{
    [Fact]
    public async Task Read_endpoints_require_documents_read_scope()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();

        using var anonymousResponse = await client.GetAsync(
            "/api/v1/documents",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        await client.LoginAsAdministratorAsync();
        var writeOnlyClient = await CreateApiClientAsync(
            client,
            "Write only",
            AuthenticationScopes.DocumentsWrite);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            writeOnlyClient.Credential);

        using var writeOnlyResponse = await client.GetAsync(
            "/api/v1/documents",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, writeOnlyResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var readClient = await CreateApiClientAsync(
            client,
            "Reader",
            AuthenticationScopes.DocumentsRead);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            readClient.Credential);

        using var readerResponse = await client.GetAsync(
            "/api/v1/documents",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, readerResponse.StatusCode);
    }

    [Fact]
    public async Task List_uses_stable_cursor_pagination_without_internal_fields()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var created = new List<DocumentResponse>();

        for (var index = 0; index < 5; index++)
        {
            created.Add(await UploadPdfAsync(
                client,
                $"page-{index}.pdf",
                Encoding.UTF8.GetBytes($"%PDF-1.7\nPage {index}\n%%EOF")));
        }

        var allItems = new List<DocumentResponse>();
        string? cursor = null;
        var pageCount = 0;

        do
        {
            var path = "/api/v1/documents?limit=2"
                + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            var json = await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
            var page = await response.Content.ReadFromJsonAsync<DocumentListResponse>(
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(page);
            Assert.DoesNotContain("storageRef", json, StringComparison.OrdinalIgnoreCase);
            Assert.True(page.Items.Count is > 0 and <= 2);
            allItems.AddRange(page.Items);
            cursor = page.NextCursor;
            pageCount++;
        }
        while (cursor is not null);

        Assert.Equal(3, pageCount);
        Assert.Equal(5, allItems.Count);
        Assert.Equal(5, allItems.Select(document => document.Id).Distinct().Count());
        Assert.Equal(
            created.Select(document => document.Id).Order().ToArray(),
            allItems.Select(document => document.Id).Order().ToArray());

        for (var index = 1; index < allItems.Count; index++)
        {
            var previous = allItems[index - 1];
            var current = allItems[index];
            Assert.True(
                previous.CreatedAt > current.CreatedAt
                || (previous.CreatedAt == current.CreatedAt
                    && previous.Id.CompareTo(current.Id) > 0));
        }
    }

    [Theory]
    [InlineData("/api/v1/documents?limit=0")]
    [InlineData("/api/v1/documents?limit=201")]
    [InlineData("/api/v1/documents?cursor=not-a-cursor")]
    public async Task List_rejects_invalid_pagination(string path)
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detail_and_content_support_private_conditional_range_downloads()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var bytes = "%PDF-1.7\nDownload content\n%%EOF"u8.ToArray();
        var created = await UploadPdfAsync(client, "download.pdf", bytes);

        using var detailResponse = await client.GetAsync(
            $"/api/v1/documents/{created.Id:D}",
            TestContext.Current.CancellationToken);
        var detailJson = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var detail = await detailResponse.Content.ReadFromJsonAsync<DocumentResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Equal(created, detail);
        Assert.DoesNotContain("storageRef", detailJson, StringComparison.OrdinalIgnoreCase);

        using var contentResponse = await client.GetAsync(
            $"/api/v1/documents/{created.Id:D}/content",
            TestContext.Current.CancellationToken);
        var downloaded = await contentResponse.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);
        Assert.Equal(bytes, downloaded);
        Assert.Equal("application/pdf", contentResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", contentResponse.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("download.pdf", contentResponse.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal($"\"{created.Sha256}\"", contentResponse.Headers.ETag?.Tag);
        Assert.Contains("bytes", contentResponse.Headers.AcceptRanges);
        Assert.True(contentResponse.Headers.CacheControl?.Private);
        Assert.Contains("nosniff", contentResponse.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("sandbox", contentResponse.Headers.GetValues("Content-Security-Policy"));

        using var conditionalRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/documents/{created.Id:D}/content");
        conditionalRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(
            $"\"{created.Sha256}\""));
        using var conditionalResponse = await client.SendAsync(
            conditionalRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, conditionalResponse.StatusCode);

        using var rangeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/documents/{created.Id:D}/content");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 4);
        using var rangeResponse = await client.SendAsync(
            rangeRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(bytes[..5], await rangeResponse.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(0, rangeResponse.Content.Headers.ContentRange?.From);
        Assert.Equal(4, rangeResponse.Content.Headers.ContentRange?.To);
        Assert.Equal(bytes.Length, rangeResponse.Content.Headers.ContentRange?.Length);

        using var missingResponse = await client.GetAsync(
            $"/api/v1/documents/{Guid.NewGuid():D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [Fact]
    public async Task Missing_stored_content_returns_generic_service_unavailable()
    {
        using var factory = new StructaDocWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var created = await UploadPdfAsync(
            client,
            "missing.pdf",
            "%PDF-1.7\nMissing content\n%%EOF"u8.ToArray());
        var storedPath = Path.Combine(
            factory.StorageRootPath,
            "documents",
            created.Id.ToString("N"),
            "original");
        File.Delete(storedPath);

        using var response = await client.GetAsync(
            $"/api/v1/documents/{created.Id:D}/content",
            TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(factory.StorageRootPath, responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageRef", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ApiClientCredentialResponse> CreateApiClientAsync(
        HttpClient client,
        string name,
        string scope)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest(name, [scope]));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApiClientCredentialResponse>()
            ?? throw new InvalidOperationException("API Client creation returned no response.");
    }

    private static async Task<DocumentResponse> UploadPdfAsync(
        HttpClient client,
        string fileName,
        byte[] bytes)
    {
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(file, "file", fileName);
        using var response = await client.PostAsync("/api/v1/documents", multipart);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DocumentResponse>()
            ?? throw new InvalidOperationException("Document upload returned no response.");
    }
}
