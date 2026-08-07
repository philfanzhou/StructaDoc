using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Storage;
using StructaDoc.Contracts.Documents;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Host.Tests;

public sealed class UserWorkspaceFeatureTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Canonical_result_api_hides_provider_json_and_document_cleanup_is_recoverable()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var document = await UploadAsync(client);
        var runId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var assetRef = $"parse-runs/{runId:N}/assets/example.png";

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            await storage.WriteAsync(assetRef, new MemoryStream("asset"u8.ToArray()), 1024);
            var db = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            db.ParseRuns.Add(new ParseRunEntity
            {
                Id = runId,
                DocumentId = document.Id,
                Status = ParseRunStatuses.Succeeded,
                ProviderType = "test-provider",
                ProviderConfigId = Guid.NewGuid(),
                ProviderConfigVersion = Guid.NewGuid(),
                OptionsJson = "{}",
                SourceMediaType = "application/pdf",
                SubmittedMediaType = "application/pdf",
                AttemptCount = 1,
                MaxAttempts = 3,
                NextAttemptAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
            });
            db.ParsePages.Add(new ParsePageEntity { ParseRunId = runId, Number = 1 });
            db.ParseAssets.Add(new ParseAssetEntity { Id = assetId, ParseRunId = runId, Name = "example.png", MediaType = "image/png", SizeBytes = 5, Sha256 = "d59386e0ae4353e9d73de00b09a1b3e91c746c0915ab91670c2c9d092323ce2a", StorageRef = assetRef, CreatedAtUtc = DateTime.UtcNow });
            db.ParseBlocks.Add(new ParseBlockEntity { Id = blockId, ParseRunId = runId, Sequence = 0, PageNumber = 1, Type = "text", Content = "stable content", ProviderDataJson = "{\"privateProviderField\":true}", SourceLocatorJson = "{\"raw\":true}" });
            await db.SaveChangesAsync();
        }

        using var result = await client.GetAsync($"/api/v1/parse-runs/{runId:D}/blocks");
        var json = await result.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("stable content", json, StringComparison.Ordinal);
        Assert.DoesNotContain("privateProviderField", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceLocator", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageRef", json, StringComparison.OrdinalIgnoreCase);

        using var deletion = await client.DeleteAsync($"/api/v1/documents/{document.Id:D}");
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);

        var completed = false;
        for (var attempt = 0; attempt < 60 && !completed; attempt++)
        {
            await Task.Delay(150);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            completed = !await db.Documents.AnyAsync(item => item.Id == document.Id)
                && await db.CleanupJobs.AnyAsync(item => item.TargetId == document.Id && item.Status == "completed");
        }
        Assert.True(completed, "The persistent cleanup worker did not finish within the test timeout.");
        Assert.False(File.Exists(Path.Combine(factory.StorageRootPath, assetRef.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static async Task<DocumentResponse> UploadAsync(HttpClient client)
    {
        using var file = new ByteArrayContent("%PDF-1.7\nworkspace\n%%EOF"u8.ToArray());
        file.Headers.ContentType = new("application/pdf");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(file, "file", "workspace.pdf");
        using var response = await client.PostAsync("/api/v1/documents", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }
}
