using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Host.ParseRuns;

public sealed class ParseExportService(IParseResultReadService results) : IParseExportService
{
    public async Task<ParseResultContent?> CreateAsync(Guid parseRunId, string format, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        switch (format.ToLowerInvariant())
        {
            case "markdown": return await results.OpenMarkdownAsync(parseRunId, access, cancellationToken);
            case "pdf":
            {
                var artifacts = await results.ListArtifactsAsync(parseRunId, access, cancellationToken);
                var pdf = artifacts?.FirstOrDefault(item => item.Type == ArtifactTypes.NormalizedPdf);
                return pdf is null ? null : await results.OpenArtifactAsync(parseRunId, pdf.Id, access, cancellationToken);
            }
            case "html": return await CreateHtmlAsync(parseRunId, access, cancellationToken);
            case "zip": return await CreateZipAsync(parseRunId, access, cancellationToken);
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private async Task<ParseResultContent?> CreateHtmlAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken)
    {
        var source = await results.OpenMarkdownAsync(parseRunId, access, cancellationToken);
        if (source is null) return null;
        await using var sourceContent = source.Content;
        using var reader = new StreamReader(sourceContent, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var markdown = await reader.ReadToEndAsync(cancellationToken);
        var body = Markdown.ToHtml(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>StructaDoc export</title><style>body{{font:16px/1.65 system-ui,sans-serif;max-width:960px;margin:40px auto;padding:0 24px;color:#17231d}}img{{max-width:100%}}pre{{overflow:auto;background:#f2f5f3;padding:16px}}table{{border-collapse:collapse}}td,th{{border:1px solid #cad4ce;padding:6px 10px}}</style></head><body>{body}</body></html>";
        return FromBytes(Encoding.UTF8.GetBytes(html), $"{parseRunId:D}.html", "text/html; charset=utf-8");
    }

    private async Task<ParseResultContent?> CreateZipAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken)
    {
        var markdown = await results.OpenMarkdownAsync(parseRunId, access, cancellationToken);
        if (markdown is null) return null;
        var assets = await results.ListAssetsAsync(parseRunId, access, cancellationToken) ?? [];
        var path = Path.Combine(Path.GetTempPath(), $"structadoc-export-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await AddEntryAsync(archive, "document.md", markdown.Content, cancellationToken);
                foreach (var asset in assets)
                {
                    var content = await results.OpenAssetAsync(parseRunId, asset.Id, access, cancellationToken);
                    if (content is null) continue;
                    await using (content.Content) await AddEntryAsync(archive, $"assets/{SafeName(asset.Name, asset.Id)}", content.Content, cancellationToken);
                }
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            stream.Position = 0;
            return new ParseResultContent(stream, $"{parseRunId:D}.zip", "application/zip", stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        finally { await markdown.Content.DisposeAsync(); }
    }

    private static async Task AddEntryAsync(ZipArchive archive, string name, Stream source, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var target = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string SafeName(string name, Guid fallback)
    {
        var safe = Path.GetFileName(name).Replace('\\', '_').Replace('/', '_');
        return string.IsNullOrWhiteSpace(safe) ? fallback.ToString("N") : safe;
    }

    private static ParseResultContent FromBytes(byte[] bytes, string name, string mediaType) => new(new MemoryStream(bytes, writable: false), name, mediaType, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
}
