using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Contracts.Documents;
using StructaDoc.Infrastructure.Persistence;

namespace StructaDoc.Host.Tests;

public sealed class DocumentUploadEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Pdf_upload_uses_detected_type_and_persists_original_bytes()
    {
        using var client = factory.CreateClient();
        var contentBytes = "%PDF-1.7\nStructaDoc test\n%%EOF"u8.ToArray();
        using var requestContent = CreateUpload(contentBytes, "../unsafe.PDF", "text/plain");

        using var response = await client.PostAsync("/api/v1/documents", requestContent);
        var responseJson = await response.Content.ReadAsStringAsync();
        var document = await response.Content.ReadFromJsonAsync<DocumentResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(document);
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
            .SingleAsync(entity => entity.Id == document.Id);
        Assert.Equal(document.Sha256, persisted.Sha256);

        await using var storedContent = File.OpenRead(
            Path.Combine(factory.StorageRootPath, persisted.StorageRef.Replace('/', Path.DirectorySeparatorChar)));
        using var storedCopy = new MemoryStream();
        await storedContent.CopyToAsync(storedCopy);
        Assert.Equal(contentBytes, storedCopy.ToArray());
    }

    [Fact]
    public async Task Unsupported_upload_is_rejected_without_creating_a_document()
    {
        using var client = factory.CreateClient();
        var countBefore = await CountDocumentsAsync();
        var fileCountBefore = CountStoredFiles();
        using var requestContent = CreateUpload("plain text"u8.ToArray(), "notes.txt", "text/plain");

        using var response = await client.PostAsync("/api/v1/documents", requestContent);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(countBefore, await CountDocumentsAsync());
        Assert.Equal(fileCountBefore, CountStoredFiles());
    }

    [Fact]
    public async Task Oversized_upload_returns_payload_too_large()
    {
        using var client = factory.CreateClient();
        using var requestContent = CreateUpload(
            new byte[(1024 * 1024) + 1],
            "large.pdf",
            "application/pdf");

        using var response = await client.PostAsync("/api/v1/documents", requestContent);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Upload_endpoint_is_not_mapped_when_development_switch_is_disabled()
    {
        using var disabledFactory = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Documents:UploadApiEnabled", "false"));
        using var client = disabledFactory.CreateClient();
        using var requestContent = CreateUpload(
            "%PDF-1.7\n%%EOF"u8.ToArray(),
            "sample.pdf",
            "application/pdf");

        using var response = await client.PostAsync("/api/v1/documents", requestContent);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
