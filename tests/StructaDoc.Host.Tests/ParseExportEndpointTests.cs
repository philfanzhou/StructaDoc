using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Contracts.Documents;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.ParseRuns;

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
        Assert.DoesNotContain("images/missing.png", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("html")]
    public async Task Export_asset_database_commands_stay_bounded_as_asset_count_grows(string format)
    {
        var singleAssetRunId = await SeedSucceededRunAsync(sourceIsPdf: true, assetCount: 1);
        var manyAssetRunId = await SeedSucceededRunAsync(sourceIsPdf: true, assetCount: 6);

        var singleAssetCommandCount = await CountExportDatabaseCommandsAsync(
            singleAssetRunId,
            format);
        var manyAssetCommandCount = await CountExportDatabaseCommandsAsync(
            manyAssetRunId,
            format);

        Assert.True(singleAssetCommandCount > 0);
        Assert.Equal(singleAssetCommandCount, manyAssetCommandCount);
    }

    [Fact]
    public async Task Export_asset_missing_from_storage_preserves_the_content_unavailable_error()
    {
        var seeded = await SeedSucceededResultAsync(sourceIsPdf: true);

        await using var serviceScope = factory.Services.CreateAsyncScope();
        var dbContext = serviceScope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var storedAsset = await dbContext.ParseAssets.AsNoTracking().SingleAsync(
            asset => asset.Id == seeded.AssetId,
            TestContext.Current.CancellationToken);
        var storage = serviceScope.ServiceProvider.GetRequiredService<IFileStorage>();
        await storage.DeleteIfExistsAsync(
            storedAsset.StorageRef,
            TestContext.Current.CancellationToken);
        var results = serviceScope.ServiceProvider.GetRequiredService<IParseResultReadService>();
        var exportAssets = await results.ListAssetsForExportAsync(
            seeded.ParseRunId,
            ResourceAccessContext.System,
            TestContext.Current.CancellationToken);
        var exportAsset = Assert.Single(exportAssets!);

        await Assert.ThrowsAsync<ParseResultContentUnavailableException>(() =>
            results.OpenExportAssetAsync(
                seeded.ParseRunId,
                exportAsset,
                TestContext.Current.CancellationToken));
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
        var contentSecurityPolicy = Assert.Single(
            response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("img-src data:", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("sandbox", contentSecurityPolicy, StringComparison.Ordinal);
        Assert.Equal(
            "no-referrer",
            Assert.Single(response.Headers.GetValues("Referrer-Policy")));

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
    public async Task Markdown_preview_cannot_emit_provider_authored_network_sources()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var seeded = await SeedSucceededResultAsync(sourceIsPdf: true);
        var unsafeMarkdown = Encoding.UTF8.GetBytes("""
            # Untrusted result

            ![](https://attacker.example/tracker.png)

            <img src="http://192.0.2.1/internal.png">
            <iframe src="https://attacker.example/frame"></iframe>
            """);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var stored = await WriteAsync(
                storage,
                $"parse-runs/{seeded.ParseRunId:N}/unsafe.md",
                unsafeMarkdown);
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var artifact = await dbContext.ParseArtifacts.SingleAsync(
                item => item.Id == seeded.MarkdownArtifactId,
                TestContext.Current.CancellationToken);
            artifact.StorageRef = stored.StorageRef;
            artifact.SizeBytes = stored.SizeBytes;
            artifact.Sha256 = stored.Sha256;
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var response = await client.GetAsync(
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/markdown/preview",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            "src=\"https://attacker.example",
            html,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "src=\"http://192.0.2.1",
            html,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;iframe", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_matching_preview_entity_tag_skips_rendering()
    {
        var exports = new CountingParseExportService();
        using var specializedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IParseExportService>();
                services.AddSingleton<IParseExportService>(exports);
            }));
        using var client = specializedFactory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var parseRunId = Guid.NewGuid();

        using var first = await client.GetAsync(
            $"/api/v1/parse-runs/{parseRunId:D}/markdown/preview",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var entityTag = first.Headers.ETag;
        Assert.Equal(1, exports.CreateCallCount);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/parse-runs/{parseRunId:D}/markdown/preview");
        request.Headers.IfNoneMatch.Add(entityTag);
        using var second = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(entityTag, second.Headers.ETag);
        // The status alone is not the optimization: this count proves the renderer was not entered.
        Assert.Equal(1, exports.CreateCallCount);
    }

    [Fact]
    public async Task Replacing_an_inlined_asset_changes_the_preview_entity_tag()
    {
        using var client = factory.CreateClient();
        await client.LoginAsAdministratorAsync();
        var seeded = await SeedSucceededResultAsync(sourceIsPdf: true);

        using var first = await client.GetAsync(
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/markdown/preview",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var firstTag = first.Headers.ETag;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var replacement = await WriteAsync(
                storage,
                $"parse-runs/{seeded.ParseRunId:N}/assets/replacement.png",
                [.. ImageBytes, 0x04]);
            var asset = await dbContext.ParseAssets.SingleAsync(
                item => item.Id == seeded.AssetId,
                TestContext.Current.CancellationToken);
            asset.StorageRef = replacement.StorageRef;
            asset.SizeBytes = replacement.SizeBytes;
            asset.Sha256 = replacement.Sha256;
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var second = await client.GetAsync(
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/markdown/preview",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(firstTag, second.Headers.ETag);
    }

    // This protects the documentation's security statement, not the Export permission itself:
    // withholding Export must never be presented as withholding result bytes from a reader.
    [Fact]
    public async Task Read_without_export_still_reaches_every_resource_an_export_uses()
    {
        using var administrator = factory.CreateClient();
        await administrator.LoginAsAdministratorAsync();
        var seeded = await SeedSucceededResultAsync(sourceIsPdf: true);

        using var createdResponse = await administrator.PostAsJsonAsync(
            "/api/v1/admin/api-clients",
            new ApiClientRequest("Read-only result consumer", [.. AuthenticationScopes.All]),
            TestContext.Current.CancellationToken);
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<ApiClientCredentialResponse>(
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("API client creation returned no response.");
        using var grantResponse = await administrator.PostAsJsonAsync(
            $"/api/v1/documents/{seeded.DocumentId:D}/access-grants",
            new DocumentAccessGrantRequest(
                PrincipalIdentity.ApiClientIssuer,
                PrincipalIdentity.ApiClientSubject(created.Client.Id),
                ["read"]),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        using var grantee = factory.CreateClient();
        grantee.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "ApiKey",
            created.Credential);

        using var export = await grantee.GetAsync(
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/exports/html",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, export.StatusCode);

        await AssertBytesAsync(
            grantee,
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/markdown",
            seeded.MarkdownBytes);
        await AssertBytesAsync(
            grantee,
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/artifacts/{seeded.MarkdownArtifactId:D}/content",
            seeded.MarkdownBytes);
        await AssertBytesAsync(
            grantee,
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/assets/{seeded.AssetId:D}/content",
            ImageBytes);

        using var preview = await grantee.GetAsync(
            $"/api/v1/parse-runs/{seeded.ParseRunId:D}/markdown/preview",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var html = await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(Convert.ToBase64String(ImageBytes), html, StringComparison.Ordinal);
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

    [Fact]
    public void Segment_rewriter_keeps_same_named_images_bound_to_their_own_segment()
    {
        var first = new ParseAssetRecord(
            Guid.NewGuid(), "figure.png", "image/png", 1, new string('a', 64), null, null);
        var second = new ParseAssetRecord(
            Guid.NewGuid(), "figure.png", "image/png", 1, new string('b', 64), null, null);

        var firstMarkdown = ExportAssetLinkRewriter.RewriteSegmentImages(
            "![](images/figure.png)",
            ExportAssetLinkRewriter.BuildAssetsByFileName([first]),
            0);
        var secondMarkdown = ExportAssetLinkRewriter.RewriteSegmentImages(
            "![](images/figure.png)",
            ExportAssetLinkRewriter.BuildAssetsByFileName([second]),
            1);

        Assert.Equal("![](segment-0000-figure.png)", firstMarkdown);
        Assert.Equal("![](segment-0001-figure.png)", secondMarkdown);
    }

    private async Task<Guid> SeedSucceededRunAsync(bool sourceIsPdf, int assetCount = 1)
        => (await SeedSucceededResultAsync(sourceIsPdf, assetCount)).ParseRunId;

    private async Task<SeededResult> SeedSucceededResultAsync(bool sourceIsPdf, int assetCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(assetCount, 1);
        var parseRunId = Guid.NewGuid();
        var markdownArtifactId = Guid.NewGuid();
        var assetIds = Enumerable.Range(0, assetCount).Select(_ => Guid.NewGuid()).ToArray();
        var assetNames = Enumerable.Range(0, assetCount)
            .Select(index => index == 0 ? "diagram.png" : $"diagram-{index + 1}.png")
            .ToArray();
        var sourceMediaType = sourceIsPdf
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        var extension = sourceIsPdf ? ".pdf" : ".docx";
        var sourceBytes = sourceIsPdf
            ? "%PDF-1.7\nexport-test"u8.ToArray()
            : "export-test-document"u8.ToArray();
        var markdownBytes = Encoding.UTF8.GetBytes(string.Join(
            "\n\n",
            ["# Export", .. assetNames.Select(name => $"![](images/{name})"), "![](images/missing.png)\n"]));

        await using var scope = factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var nowUtc = DateTime.UtcNow;

        var storedSource = await WriteAsync(storage, $"documents/{parseRunId:N}/source{extension}", sourceBytes);
        var storedMarkdown = await WriteAsync(storage, $"parse-runs/{parseRunId:N}/markdown.md", markdownBytes);
        var storedImages = new StoredFile[assetCount];
        for (var index = 0; index < assetCount; index++)
        {
            storedImages[index] = await WriteAsync(
                storage,
                $"parse-runs/{parseRunId:N}/assets/{assetNames[index]}",
                ImageBytes);
        }

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
            Id = markdownArtifactId,
            ParseRunId = parseRunId,
            Type = ArtifactTypes.Markdown,
            Name = "document.md",
            MediaType = "text/markdown",
            SizeBytes = storedMarkdown.SizeBytes,
            Sha256 = storedMarkdown.Sha256,
            StorageRef = storedMarkdown.StorageRef,
            CreatedAtUtc = nowUtc,
        });
        for (var index = 0; index < assetCount; index++)
        {
            dbContext.ParseAssets.Add(new ParseAssetEntity
            {
                Id = assetIds[index],
                ParseRunId = parseRunId,
                Name = assetNames[index],
                MediaType = "image/png",
                SizeBytes = storedImages[index].SizeBytes,
                Sha256 = storedImages[index].Sha256,
                StorageRef = storedImages[index].StorageRef,
                CreatedAtUtc = nowUtc,
            });
        }
        await dbContext.SaveChangesAsync();

        return new SeededResult(
            parseRunId,
            document.Id,
            markdownArtifactId,
            assetIds[0],
            markdownBytes);
    }

    private async Task<int> CountExportDatabaseCommandsAsync(
        Guid parseRunId,
        string format)
    {
        await using var serviceScope = factory.Services.CreateAsyncScope();
        var exports = serviceScope.ServiceProvider.GetRequiredService<IParseExportService>();
        using var commandScope = factory.DatabaseCommandCounter.BeginScope();
        var export = await exports.CreateAsync(
            parseRunId,
            format,
            ResourceAccessContext.System,
            TestContext.Current.CancellationToken);
        Assert.NotNull(export);
        await using var content = export.Content;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return commandScope.CommandCount;
    }

    private static async Task AssertBytesAsync(HttpClient client, string path, byte[] expected)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            expected,
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<StoredFile> WriteAsync(IFileStorage storage, string storageRef, byte[] content)
    {
        await using var stream = new MemoryStream(content, writable: false);
        return await storage.WriteAsync(storageRef, stream, content.Length);
    }

    private sealed record SeededResult(
        Guid ParseRunId,
        Guid DocumentId,
        Guid MarkdownArtifactId,
        Guid AssetId,
        byte[] MarkdownBytes);

    private sealed class CountingParseExportService : IParseExportService
    {
        private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public int CreateCallCount { get; private set; }

        public Task<string?> GetHtmlEntityTagAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(Fingerprint);

        public Task<ParseResultContent?> CreateAsync(Guid parseRunId, string format, ResourceAccessContext access, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            var bytes = "<!doctype html><p>preview</p>"u8.ToArray();
            return Task.FromResult<ParseResultContent?>(new ParseResultContent(
                new MemoryStream(bytes, writable: false),
                $"{parseRunId:D}.html",
                "text/html; charset=utf-8",
                bytes.Length,
                Fingerprint));
        }
    }
}
