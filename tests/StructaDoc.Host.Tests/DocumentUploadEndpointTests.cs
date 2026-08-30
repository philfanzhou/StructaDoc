using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Documents;

namespace StructaDoc.Host.Tests;

public sealed class DocumentUploadEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Pdf_upload_uses_detected_type_and_persists_original_bytes()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var contentBytes = "%PDF-1.7\nStructaDoc test\n%%EOF"u8.ToArray();
        using var requestContent = CreateUpload(contentBytes, "../unsafe.PDF", "text/plain");

        using var response = await client.PostAsync(
            "/api/v1/documents",
            requestContent,
            TestContext.Current.CancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var document = await response.Content.ReadFromJsonAsync<DocumentResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(document);
        Assert.Equal(
            $"/api/v1/documents/{document.Id:D}",
            response.Headers.Location?.OriginalString);
        Assert.Equal("unsafe.PDF", document.OriginalFileName);
        Assert.Equal("application/pdf", document.MediaType);
        Assert.Equal(".pdf", document.Extension);
        Assert.Equal(contentBytes.Length, document.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant(),
            document.Sha256);
        Assert.Contains("\"createdAt\"", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("createdAtUtc", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("storageRef", responseJson, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var persisted = await dbContext.Documents
            .AsNoTracking()
            .SingleAsync(
                entity => entity.Id == document.Id,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(document.Sha256, persisted.Sha256);
        Assert.StartsWith("administrator:", persisted.CreatedBy, StringComparison.Ordinal);

        await using var storedContent = File.OpenRead(
            Path.Combine(factory.StorageRootPath, persisted.StorageRef.Replace('/', Path.DirectorySeparatorChar)));
        using var storedCopy = new MemoryStream();
        await storedContent.CopyToAsync(storedCopy, TestContext.Current.CancellationToken);
        Assert.Equal(contentBytes, storedCopy.ToArray());
    }

    [Fact]
    public async Task Unsupported_upload_is_rejected_without_creating_a_document()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var countBefore = await CountDocumentsAsync();
        var fileCountBefore = CountStoredFiles();
        using var requestContent = CreateUpload("plain text"u8.ToArray(), "notes.txt", "text/plain");

        using var response = await client.PostAsync(
            "/api/v1/documents",
            requestContent,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(countBefore, await CountDocumentsAsync());
        Assert.Equal(fileCountBefore, CountStoredFiles());
    }

    [Fact]
    public async Task Oversized_upload_returns_payload_too_large()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        using var requestContent = CreateUpload(
            new byte[(1024 * 1024) + 1],
            "large.pdf",
            "application/pdf");

        using var response = await client.PostAsync(
            "/api/v1/documents",
            requestContent,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Upload_post_is_not_allowed_when_development_switch_is_disabled()
    {
        using var disabledFactory = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Documents:UploadApiEnabled", "false"));
        using var client = disabledFactory.CreateClient();
        using var requestContent = CreateUpload(
            "%PDF-1.7\n%%EOF"u8.ToArray(),
            "sample.pdf",
            "application/pdf");

        using var response = await client.PostAsync(
            "/api/v1/documents",
            requestContent,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Upload_requires_an_authenticated_subject()
    {
        using var client = factory.CreateClient();
        using var requestContent = CreateUpload(
            "%PDF-1.7\n%%EOF"u8.ToArray(),
            "sample.pdf",
            "application/pdf");

        using var response = await client.PostAsync(
            "/api/v1/documents",
            requestContent,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Administrator_upload_requires_antiforgery_token()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var requestContent = CreateUpload(
            "%PDF-1.7\n%%EOF"u8.ToArray(),
            "sample.pdf",
            "application/pdf");

        using var response = await client.PostAsync(
            "/api/v1/documents",
            requestContent,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_client_with_documents_write_scope_can_upload_without_antiforgery()
    {
        using var client = factory.CreateClient();
        var apiKey = await CreateApiClientAsync(AuthenticationScopes.DocumentsWrite);
        using var requestContent = CreateUpload(
            "%PDF-1.7\nAPI client\n%%EOF"u8.ToArray(),
            "api-client.pdf",
            "application/pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = requestContent,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            apiKey.Credential);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var document = await response.Content.ReadFromJsonAsync<DocumentResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(document);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var createdBy = await dbContext.Documents
            .Where(entity => entity.Id == document.Id)
            .Select(entity => entity.CreatedBy)
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal($"api-client:{apiKey.ClientId:D}", createdBy);
    }

    [Fact]
    public async Task Api_client_without_documents_write_scope_is_forbidden()
    {
        using var client = factory.CreateClient();
        var apiKey = await CreateApiClientAsync(AuthenticationScopes.DocumentsRead);
        using var requestContent = CreateUpload(
            "%PDF-1.7\nRead only\n%%EOF"u8.ToArray(),
            "read-only.pdf",
            "application/pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = requestContent,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            apiKey.Credential);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_api_key_is_unauthorized()
    {
        using var client = factory.CreateClient();
        var clientId = Guid.NewGuid();
        var apiKey = ApiKeyCredential.Create(clientId);
        using var requestContent = CreateUpload(
            "%PDF-1.7\nInvalid key\n%%EOF"u8.ToArray(),
            "invalid-key.pdf",
            "application/pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = requestContent,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            apiKey.Credential);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_api_client_is_unauthorized()
    {
        using var client = factory.CreateClient();
        var apiKey = await CreateApiClientAsync(AuthenticationScopes.DocumentsWrite);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var apiClient = await dbContext.ApiClients.SingleAsync(
                entity => entity.Id == apiKey.ClientId,
                cancellationToken: TestContext.Current.CancellationToken);
            apiClient.RevokedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var requestContent = CreateUpload(
            "%PDF-1.7\nRevoked key\n%%EOF"u8.ToArray(),
            "revoked-key.pdf",
            "application/pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = requestContent,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            apiKey.Credential);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<int> CountDocumentsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        return await dbContext.Documents.CountAsync();
    }

    private int CountStoredFiles()
    {
        return Directory.Exists(factory.StorageRootPath)
            ? Directory.GetFiles(factory.StorageRootPath, "*", SearchOption.AllDirectories).Length
            : 0;
    }

    private async Task<IssuedApiKey> CreateApiClientAsync(string scopes)
    {
        var clientId = Guid.NewGuid();
        var apiKey = ApiKeyCredential.Create(clientId);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        dbContext.ApiClients.Add(new ApiClientEntity
        {
            Id = clientId,
            Name = $"Test client {clientId:N}",
            SecretHash = apiKey.SecretHash,
            Scopes = scopes,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return apiKey;
    }

    private static MultipartFormDataContent CreateUpload(
        byte[] content,
        string fileName,
        string mediaType)
    {
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        var multipart = new MultipartFormDataContent();
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }
}
