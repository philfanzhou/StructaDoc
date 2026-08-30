using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Host.ParseRuns;

public sealed class ParseExportService(
    IParseResultReadService results,
    IDocumentReadService documents) : IParseExportService
{
    // Increment this whenever HTML rendering, link rewriting, or asset inlining changes. Existing
    // entity tags must stop validating when the same inputs would render different bytes.
    private const int HtmlRendererVersion = 2;

    /// <summary>
    /// Total inlined image bytes allowed in one HTML export. A self-contained page must stay
    /// bounded, so images beyond the budget are omitted instead of being fetched by the browser.
    /// </summary>
    private const long MaximumInlinedAssetBytes = 32L * 1024 * 1024;

    private const long MaximumInlinedAssetItemBytes = 8L * 1024 * 1024;

    public async Task<string?> GetHtmlEntityTagAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        var artifacts = await results.ListArtifactsAsync(parseRunId, access, cancellationToken);
        var markdown = artifacts?.FirstOrDefault(item => item.Type == ArtifactTypes.Markdown);
        if (markdown is null) return null;

        var assets = await results.ListAssetsAsync(parseRunId, access, cancellationToken);
        if (assets is null) return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashComponent(hash, HtmlRendererVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendHashComponent(hash, MaximumInlinedAssetBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendHashComponent(hash, MaximumInlinedAssetItemBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendHashComponent(hash, markdown.Sha256);
        foreach (var asset in SelectInlineableAssets(OrderAssets(assets)))
        {
            AppendHashComponent(hash, asset.Sha256);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public async Task<ParseResultContent?> CreateAsync(Guid parseRunId, string format, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        switch (format.ToLowerInvariant())
        {
            case "markdown": return await results.OpenMarkdownAsync(parseRunId, access, cancellationToken);
            case "pdf": return await CreatePdfAsync(parseRunId, access, cancellationToken);
            case "html": return await CreateHtmlAsync(parseRunId, access, cancellationToken);
            case "zip": return await CreateZipAsync(parseRunId, access, cancellationToken);
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Returns the PDF representation of a Parse Run: the converted <c>normalized-pdf</c> Artifact
    /// when an Office source required conversion, otherwise the original when it is already a PDF.
    /// </summary>
    private async Task<ParseResultContent?> CreatePdfAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken)
    {
        var artifacts = await results.ListArtifactsAsync(parseRunId, access, cancellationToken);
        if (artifacts is null) return null;
        var pdf = artifacts.FirstOrDefault(item => item.Type == ArtifactTypes.NormalizedPdf);
        if (pdf is not null) return await results.OpenArtifactAsync(parseRunId, pdf.Id, access, cancellationToken);

        var parseRun = await results.GetAsync(parseRunId, access, cancellationToken);
        if (parseRun is null
            || !string.Equals(parseRun.SourceMediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var original = await documents.OpenAccessibleContentAsync(parseRun.DocumentId, access, cancellationToken);
        if (original is null) return null;
        return new ParseResultContent(
            original.Content,
            original.Document.OriginalFileName,
            original.Document.MediaType,
            original.Document.SizeBytes,
            original.Document.Sha256);
    }

    private async Task<ParseResultContent?> CreateHtmlAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken)
    {
        var markdown = await ReadMarkdownAsync(parseRunId, access, cancellationToken);
        if (markdown is null) return null;
        var exportAssets = OrderExportAssets(await results.ListAssetsForExportAsync(parseRunId, access, cancellationToken) ?? []);
        var assets = exportAssets.Select(asset => asset.Metadata).ToArray();
        var inlined = await InlineAssetsAsync(parseRunId, exportAssets, cancellationToken);
        var source = ExportAssetLinkRewriter.Rewrite(
            markdown,
            ExportAssetLinkRewriter.BuildAssetsByFileName(assets),
            asset => inlined.TryGetValue(asset.Id, out var dataUri) ? dataUri : null);
        source = ExportAssetLinkRewriter.RemoveNonEmbeddedImages(source);
        var body = Markdown.ToHtml(
            source,
            new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build());
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>StructaDoc export</title><style>body{{font:16px/1.65 system-ui,sans-serif;max-width:960px;margin:40px auto;padding:0 24px;color:#17231d}}img{{max-width:100%}}pre{{overflow:auto;background:#f2f5f3;padding:16px}}table{{border-collapse:collapse}}td,th{{border:1px solid #cad4ce;padding:6px 10px}}</style></head><body>{body}</body></html>";
        return FromBytes(Encoding.UTF8.GetBytes(html), $"{parseRunId:D}.html", "text/html; charset=utf-8");
    }

    private async Task<ParseResultContent?> CreateZipAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken)
    {
        var markdown = await ReadMarkdownAsync(parseRunId, access, cancellationToken);
        if (markdown is null) return null;
        var exportAssets = await results.ListAssetsForExportAsync(parseRunId, access, cancellationToken) ?? [];
        var assets = exportAssets.Select(asset => asset.Metadata).ToArray();
        var entryNames = BuildUniqueEntryNames(assets);
        var document = ExportAssetLinkRewriter.Rewrite(
            markdown,
            ExportAssetLinkRewriter.BuildAssetsByFileName(assets),
            asset => entryNames.TryGetValue(asset.Id, out var name)
                ? ExportAssetLinkRewriter.EncodeUriPath($"assets/{name}")
                : null);

        var path = Path.Combine(Path.GetTempPath(), $"structadoc-export-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await using (var documentStream = new MemoryStream(Encoding.UTF8.GetBytes(document), writable: false))
                    await AddEntryAsync(archive, "document.md", documentStream, cancellationToken);
                foreach (var asset in exportAssets)
                {
                    if (!entryNames.TryGetValue(asset.Id, out var name)) continue;
                    var content = await results.OpenExportAssetAsync(parseRunId, asset, cancellationToken);
                    if (content is null) continue;
                    await using (content.Content) await AddEntryAsync(archive, $"assets/{name}", content.Content, cancellationToken);
                }
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            stream.Position = 0;
            return new ParseResultContent(stream, $"{parseRunId:D}.zip", "application/zip", stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    private async Task<string?> ReadMarkdownAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken)
    {
        var source = await results.OpenMarkdownAsync(parseRunId, access, cancellationToken);
        if (source is null) return null;
        await using var content = source.Content;
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> InlineAssetsAsync(
        Guid parseRunId,
        IReadOnlyList<ParseExportAssetRecord> assets,
        CancellationToken cancellationToken)
    {
        var inlined = new Dictionary<Guid, string>();
        var remainingBytes = MaximumInlinedAssetBytes;

        foreach (var asset in assets)
        {
            if (asset.SizeBytes > MaximumInlinedAssetItemBytes || asset.SizeBytes > remainingBytes) continue;
            var content = await results.OpenExportAssetAsync(parseRunId, asset, cancellationToken);
            if (content is null) continue;
            await using var assetContent = content.Content;
            using var buffer = new MemoryStream();
            await assetContent.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > remainingBytes) continue;
            remainingBytes -= buffer.Length;
            inlined[asset.Id] = ExportAssetLinkRewriter.ToDataUri(asset.MediaType, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        return inlined;
    }

    private static IReadOnlyList<ParseAssetRecord> OrderAssets(IReadOnlyList<ParseAssetRecord> assets) =>
        [.. assets.OrderBy(asset => asset.Name, StringComparer.Ordinal).ThenBy(asset => asset.Id)];

    private static IReadOnlyList<ParseExportAssetRecord> OrderExportAssets(IReadOnlyList<ParseExportAssetRecord> assets) =>
        [.. assets.OrderBy(asset => asset.Name, StringComparer.Ordinal).ThenBy(asset => asset.Id)];

    private static IEnumerable<ParseAssetRecord> SelectInlineableAssets(IReadOnlyList<ParseAssetRecord> assets)
    {
        var remainingBytes = MaximumInlinedAssetBytes;
        foreach (var asset in assets)
        {
            if (asset.SizeBytes > MaximumInlinedAssetItemBytes || asset.SizeBytes > remainingBytes) continue;
            remainingBytes -= asset.SizeBytes;
            yield return asset;
        }
    }

    private static void AppendHashComponent(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static Dictionary<Guid, string> BuildUniqueEntryNames(IReadOnlyList<ParseAssetRecord> assets)
    {
        var names = new Dictionary<Guid, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            var name = SafeName(asset.Name, asset.Id);
            if (!used.Add(name))
            {
                name = $"{asset.Id:N}-{name}";
                used.Add(name);
            }

            names[asset.Id] = name;
        }

        return names;
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
