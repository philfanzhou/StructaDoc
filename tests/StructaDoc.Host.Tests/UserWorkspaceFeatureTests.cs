using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;
using StructaDoc.Contracts.Documents;
using StructaDoc.Domain.ParseRuns;

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
            await storage.WriteAsync(
                assetRef,
                new MemoryStream("asset"u8.ToArray()),
                1024,
                TestContext.Current.CancellationToken);
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
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var result = await client.GetAsync(
            $"/api/v1/parse-runs/{runId:D}/blocks",
            TestContext.Current.CancellationToken);
        var json = await result.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("stable content", json, StringComparison.Ordinal);
        Assert.DoesNotContain("privateProviderField", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceLocator", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageRef", json, StringComparison.OrdinalIgnoreCase);

        using var deletion = await client.DeleteAsync(
            $"/api/v1/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);

        var completed = false;
        for (var attempt = 0; attempt < 60 && !completed; attempt++)
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            completed = !await db.Documents.AnyAsync(
                item => item.Id == document.Id,
                cancellationToken: TestContext.Current.CancellationToken)
                && await db.CleanupJobs.AnyAsync(
                    item => item.TargetId == document.Id && item.Status == "completed",
                    cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.True(completed, "The persistent cleanup worker did not finish within the test timeout.");
        Assert.False(File.Exists(Path.Combine(factory.StorageRootPath, assetRef.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Deleting_the_only_succeeded_parse_run_removes_every_stored_object_and_row()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var document = await UploadAsync(client);
        var runId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var conversionArtifactId = Guid.NewGuid();
        // Everything a succeeded run can leave behind: the image, the canonical Markdown, the
        // Provider archive, a PDF segment with its own archive, and the converted PDF.
        var refs = new[]
        {
            $"parse-runs/{runId:N}/assets/example.png",
            $"parse-runs/{runId:N}/artifacts/document.md",
            $"parse-runs/{runId:N}/provider/result.zip",
            $"parse-runs/{runId:N}/segments/0000.pdf",
            $"parse-runs/{segmentId:N}/provider/result.zip",
            $"parse-runs/{runId:N}/conversions/{conversionArtifactId:N}.pdf",
        };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            foreach (var storageRef in refs)
            {
                await storage.WriteAsync(
                    storageRef,
                    new MemoryStream("payload"u8.ToArray()),
                    1024,
                    TestContext.Current.CancellationToken);
            }

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
                SourceMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                SubmittedMediaType = "application/pdf",
                ConversionJson = new ParseRunConversion(
                    "libreoffice",
                    "24.8",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    "application/pdf",
                    conversionArtifactId,
                    "normalized.pdf",
                    7,
                    PayloadSha256,
                    refs[5],
                    "pdf").ToJson(),
                AttemptCount = 1,
                MaxAttempts = 3,
                NextAttemptAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
            });
            db.ParsePages.Add(new ParsePageEntity { ParseRunId = runId, Number = 1 });
            db.ParseAssets.Add(new ParseAssetEntity { Id = assetId, ParseRunId = runId, Name = "example.png", MediaType = "image/png", SizeBytes = 7, Sha256 = PayloadSha256, StorageRef = refs[0], CreatedAtUtc = DateTime.UtcNow });
            // The Block points at the Asset, which is the one relationship inside a run that is not
            // a cascade. A deletion that leaves the Asset row unreachable would fail here.
            db.ParseBlocks.Add(new ParseBlockEntity { Id = Guid.NewGuid(), ParseRunId = runId, Sequence = 0, PageNumber = 1, Type = "image", AssetId = assetId });
            db.ParseArtifacts.Add(new ParseArtifactEntity { Id = Guid.NewGuid(), ParseRunId = runId, Type = "markdown", Name = "document.md", MediaType = "text/markdown", SizeBytes = 7, Sha256 = PayloadSha256, StorageRef = refs[1], CreatedAtUtc = DateTime.UtcNow });
            db.ParseSegments.Add(new ParseSegmentEntity { Id = segmentId, ParseRunId = runId, Index = 0, StartPage = 1, EndPage = 1, StorageRef = refs[3], SizeBytes = 7, Sha256 = PayloadSha256, Status = ParseRunStatuses.Succeeded, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var deletion = await client.DeleteAsync(
            $"/api/v1/parse-runs/{runId:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);

        var completed = false;
        for (var attempt = 0; attempt < 60 && !completed; attempt++)
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            completed = !await db.ParseRuns.AnyAsync(
                item => item.Id == runId,
                cancellationToken: TestContext.Current.CancellationToken)
                && await db.CleanupJobs.AnyAsync(
                    item => item.TargetId == runId && item.Status == "completed",
                    cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.True(completed, "The persistent cleanup worker did not finish within the test timeout.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            Assert.False(await db.ParsePages.AnyAsync(
                item => item.ParseRunId == runId,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(await db.ParseBlocks.AnyAsync(
                item => item.ParseRunId == runId,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(await db.ParseAssets.AnyAsync(
                item => item.ParseRunId == runId,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(await db.ParseArtifacts.AnyAsync(
                item => item.ParseRunId == runId,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(await db.ParseSegments.AnyAsync(
                item => item.ParseRunId == runId,
                cancellationToken: TestContext.Current.CancellationToken));
            // The Document itself survives losing its last Parse Run and can be parsed again.
            Assert.True(await db.Documents.AnyAsync(
                item => item.Id == document.Id,
                cancellationToken: TestContext.Current.CancellationToken));
        }

        foreach (var storageRef in refs)
        {
            Assert.False(
                File.Exists(Path.Combine(factory.StorageRootPath, storageRef.Replace('/', Path.DirectorySeparatorChar))),
                $"Stored object '{storageRef}' outlived the Parse Run it belonged to.");
        }

        Assert.False(
            Directory.Exists(Path.Combine(factory.StorageRootPath, "parse-runs", runId.ToString("N"))),
            "The Parse Run directory outlived every object stored under it.");

        using var runs = await client.GetAsync(
            $"/api/v1/documents/{document.Id:D}/parse-runs",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
        Assert.Equal("[]", (await runs.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken)).Trim());
    }

    private const string PayloadSha256 = "239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426952f9b852d5a935e5";

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
