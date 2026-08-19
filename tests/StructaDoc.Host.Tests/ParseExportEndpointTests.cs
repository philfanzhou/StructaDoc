using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.ParseRuns;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;

namespace StructaDoc.Host.Tests;

public sealed class ParseExportEndpointTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    private static readonly byte[] ImageBytes =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01, 0x02, 0x03];

    [Fact]
    public async Task Zip_export_points_markdown_at_its_bundled_assets()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var parseRunId = await SeedSucceededRunAsync(sourceIsPdf: true);

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRunId:D}/exports/zip",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var content = await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("document.md", entryNames);
        Assert.Contains("assets/diagram.png", entryNames);

        await using var documentEntry = archive.GetEntry("document.md")!.Open();
        using var reader = new StreamReader(documentEntry);
        var markdown = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // The Provider-relative path resolves to nothing outside its archive; the export must point
        // the Markdown at the copy it actually bundles.
        Assert.Contains("![](assets/diagram.png)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("images/diagram.png", markdown, StringComparison.Ordinal);
        // An unresolvable reference is preserved rather than silently dropped.
        Assert.Contains("images/missing.png", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_export_inlines_assets_so_the_page_is_self_contained()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var parseRunId = await SeedSucceededRunAsync(sourceIsPdf: true);

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRunId:D}/exports/html",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            $"data:image/png;base64,{Convert.ToBase64String(ImageBytes)}",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"images/diagram.png\"", html, StringComparison.Ordinal);
        Assert.Contains("images/missing.png", html, StringComparison.Ordinal);
    }

    // The preview is the HTML export served for display. What is worth testing is not the rendering,
    // which the export tests above already cover, but the two things that differ: it is reached by
    // reading a result rather than by exporting one, and it arrives as a page rather than as a file.
    [Fact]
    public async Task Markdown_preview_serves_the_rendered_result_inline_rather_than_as_a_download()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var parseRunId = await SeedSucceededRunAsync(sourceIsPdf: true);

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRunId:D}/markdown/preview",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        // A browser that saves the preview has not shown one.
        Assert.NotEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        // The page carries Provider-authored content, so it is served into an opaque origin where
        // it cannot reach the session that asked for it.
        Assert.Equal("sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<h1", html, StringComparison.Ordinal);
        // Inlined rather than linked: a request back to this service from an opaque origin carries
        // no session cookie, so a linked image would be a broken image.
        Assert.Contains(
            $"data:image/png;base64,{Convert.ToBase64String(ImageBytes)}",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markdown_preview_is_unavailable_for_a_parse_run_the_caller_cannot_read()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{Guid.NewGuid():D}/markdown/preview",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Markdown_preview_is_not_served_without_a_credential()
    {
        using var client = factory.CreateClient();
        var parseRunId = await SeedSucceededRunAsync(sourceIsPdf: true);

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRunId:D}/markdown/preview",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_export_returns_the_original_when_the_source_is_already_pdf()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var parseRunId = await SeedSucceededRunAsync(sourceIsPdf: true);

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRunId:D}/exports/pdf",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken);
        Assert.StartsWith("%PDF-1.7", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_export_is_unavailable_without_a_pdf_representation()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var parseRunId = await SeedSucceededRunAsync(sourceIsPdf: false);

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRunId:D}/exports/pdf",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("![](images/diagram.png)", "![](assets/diagram.png)")]
    [InlineData("![alt](./nested/images/diagram.png)", "![alt](assets/diagram.png)")]
    [InlineData("![](images/diagram.png \"title\")", "![](assets/diagram.png \"title\")")]
    [InlineData("<img src=\"images/diagram.png\">", "<img src=\"assets/diagram.png\">")]
    [InlineData("<IMG WIDTH=10 SRC='images/diagram.png'/>", "<IMG WIDTH=10 SRC='assets/diagram.png'/>")]
    [InlineData("![](images/diagram.png?v=2)", "![](assets/diagram.png)")]
    public void Rewriter_maps_provider_relative_image_links(string source, string expected)
    {
        var asset = new ParseAssetRecord(
            Guid.NewGuid(),
            "diagram.png",
            "image/png",
            ImageBytes.Length,
            new string('a', 64),
            null,
            null);

        var rewritten = ExportAssetLinkRewriter.Rewrite(
            source,
            ExportAssetLinkRewriter.BuildAssetsByFileName([asset]),
            _ => "assets/diagram.png");

        Assert.Equal(expected, rewritten);
    }

    [Fact]
    public void Rewriter_leaves_absolute_unmatched_and_ambiguous_links_alone()
    {
        var first = new ParseAssetRecord(
            Guid.NewGuid(), "shared.png", "image/png", 1, new string('a', 64), null, null);
        var second = new ParseAssetRecord(
            Guid.NewGuid(), "shared.png", "image/png", 1, new string('b', 64), null, null);
        var unique = new ParseAssetRecord(
            Guid.NewGuid(), "unique.png", "image/png", 1, new string('c', 64), null, null);
        var assets = ExportAssetLinkRewriter.BuildAssetsByFileName([first, second, unique]);

        const string source =
            "![](https://cdn.example/unique.png) ![](images/unknown.png) ![](images/shared.png) ![](images/unique.png)";
        var rewritten = ExportAssetLinkRewriter.Rewrite(source, assets, _ => "assets/replaced.png");

        Assert.Contains("https://cdn.example/unique.png", rewritten, StringComparison.Ordinal);
        Assert.Contains("images/unknown.png", rewritten, StringComparison.Ordinal);
        // Two Assets share this file name, so the link is ambiguous and is not guessed.
        Assert.Contains("images/shared.png", rewritten, StringComparison.Ordinal);
        Assert.Contains("assets/replaced.png", rewritten, StringComparison.Ordinal);
    }

    private async Task<Guid> SeedSucceededRunAsync(bool sourceIsPdf)
    {
        var parseRunId = Guid.NewGuid();
        var sourceMediaType = sourceIsPdf
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var extension = sourceIsPdf ? ".pdf" : ".docx";
        var sourceBytes = sourceIsPdf
            ? "%PDF-1.7\nexport-test"u8.ToArray()
            : "export-test-document"u8.ToArray();
        var markdownBytes = Encoding.UTF8.GetBytes(
            "# Export\n\n![](images/diagram.png)\n\n![](images/missing.png)\n");

        await using var scope = factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var nowUtc = DateTime.UtcNow;

        var storedSource = await WriteAsync(storage, $"documents/{parseRunId:N}/source{extension}", sourceBytes);
        var storedMarkdown = await WriteAsync(storage, $"parse-runs/{parseRunId:N}/markdown.md", markdownBytes);
        var storedImage = await WriteAsync(storage, $"parse-runs/{parseRunId:N}/assets/diagram.png", ImageBytes);

        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            OriginalFileName = $"export-test{extension}",
            MediaType = sourceMediaType,
            Extension = extension,
            SizeBytes = storedSource.SizeBytes,
            Sha256 = storedSource.Sha256,
            StorageRef = storedSource.StorageRef,
            CreatedAtUtc = nowUtc,
        };
        dbContext.Documents.Add(document);
        dbContext.ParseRuns.Add(new ParseRunEntity
        {
            Id = parseRunId,
            DocumentId = document.Id,
            Status = ParseRunStatuses.Succeeded,
            ProviderType = "test-provider",
            ProviderConfigId = Guid.NewGuid(),
            ProviderConfigVersion = Guid.NewGuid(),
            OptionsJson = "{}",
            SourceMediaType = sourceMediaType,
            SubmittedMediaType = sourceMediaType,
            MaxAttempts = 3,
            NextAttemptAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            CompletedAtUtc = nowUtc,
        });
        dbContext.ParseArtifacts.Add(new ParseArtifactEntity
        {
            Id = Guid.NewGuid(),
            ParseRunId = parseRunId,
            Type = ArtifactTypes.Markdown,
            Name = "document.md",
            MediaType = "text/markdown",
            SizeBytes = storedMarkdown.SizeBytes,
            Sha256 = storedMarkdown.Sha256,
            StorageRef = storedMarkdown.StorageRef,
            CreatedAtUtc = nowUtc,
        });
        dbContext.ParseAssets.Add(new ParseAssetEntity
        {
            Id = Guid.NewGuid(),
            ParseRunId = parseRunId,
            Name = "diagram.png",
            MediaType = "image/png",
            SizeBytes = storedImage.SizeBytes,
            Sha256 = storedImage.Sha256,
            StorageRef = storedImage.StorageRef,
            CreatedAtUtc = nowUtc,
        });
        await dbContext.SaveChangesAsync();

        return parseRunId;
    }

    private static async Task<StoredFile> WriteAsync(IFileStorage storage, string storageRef, byte[] content)
    {
        await using var stream = new MemoryStream(content, writable: false);
        return await storage.WriteAsync(storageRef, stream, content.Length);
    }
}
