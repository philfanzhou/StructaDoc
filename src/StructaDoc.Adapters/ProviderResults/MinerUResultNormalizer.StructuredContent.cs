using System.Text;
using System.Text.Json;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;

namespace StructaDoc.Adapters.ProviderResults;

public sealed partial class MinerUResultNormalizer
{
    // MinerU 4.0.12 / docvortex 0.5.16 renders each top-level Block into a string.
    // This is deliberately separate from the legacy Content List field/format rules.
    private static (IReadOnlyList<ParsePage> Pages, IReadOnlyList<ParseBlock> Blocks) CreateStructuredContent(
        Guid parseRunId,
        JsonElement root,
        IReadOnlyDictionary<string, Guid> assetsByPath,
        CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("pages", out var sourcePages)
            || sourcePages.ValueKind != JsonValueKind.Array)
        {
            throw InvalidStructuredContent();
        }

        if (sourcePages.GetArrayLength() > ParseBundleValidator.MaxPages)
        {
            throw Failure("mineru-result-page-limit", "The MinerU result exceeds the supported Page count.");
        }

        var physical = HasPhysicalPages(root);
        var pages = new List<ParsePage>();
        var blocks = new List<ParseBlock>();
        var previousPageIndex = -1;
        foreach (var page in sourcePages.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.ValueKind != JsonValueKind.Object
                || !page.TryGetProperty("blocks", out var sourceBlocks)
                || sourceBlocks.ValueKind != JsonValueKind.Array)
            {
                throw InvalidStructuredContent();
            }

            var pageIndex = GetOptionalInt32(page, "page_idx");
            if (pageIndex is null or < 0 or int.MaxValue || pageIndex <= previousPageIndex)
            {
                throw Failure("mineru-result-page-invalid", "A MinerU page has an invalid page identifier.");
            }

            previousPageIndex = pageIndex.Value;
            var locator = JsonSerializer.Serialize(new { providerPageId = pageIndex.Value });
            int? pageNumber = physical ? pageIndex.Value + 1 : null;
            if (physical)
            {
                pages.Add(new ParsePage(pageNumber!.Value, SourceLocatorJson: locator));
            }

            if (sourceBlocks.GetArrayLength() > ParseBundleValidator.MaxBlocks - blocks.Count)
            {
                throw Failure("mineru-result-block-limit", "The MinerU result exceeds the supported Block count.");
            }

            foreach (var source in sourceBlocks.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.ValueKind != JsonValueKind.Object
                    || NormalizeOptionalToken(GetString(source, "type")) is not { } providerType
                    || !source.TryGetProperty("content", out var contentValue)
                    || contentValue.ValueKind != JsonValueKind.String)
                {
                    throw Failure("mineru-result-block-invalid", "A MinerU structured-content Block is invalid.");
                }

                var type = providerType switch
                {
                    "doc-title" or "paragraph-title" => "title",
                    "text" or "ref-text" => "text",
                    "list" or "table" or "code" => providerType,
                    "equation" => "formula",
                    "image" or "chart" => "image",
                    "header" or "footer" => providerType,
                    "page-number" => "page-number",
                    "page-footnote" => "footnote",
                    _ => "unknown",
                };
                var level = GetOptionalInt32(source, "level");
                var subtype = type == "title" && level > 0
                    ? $"heading-{Math.Min(level.Value, 99)}"
                    : providerType is "ref-text" or "chart" || type == "unknown"
                        ? providerType
                        : NormalizeOptionalToken(GetString(source, "sub_type"));
                var content = contentValue.GetString()!;
                var format = type == "formula" ? "latex" : type == "image" && content.Length == 0 ? null : "markdown";
                var hasAnnotations = providerType is "image" or "chart" or "table" or "code";
                blocks.Add(new ParseBlock(
                    CreateDeterministicId(parseRunId, $"block:{blocks.Count}"),
                    blocks.Count,
                    pageNumber,
                    type,
                    subtype,
                    content,
                    format,
                    MapStructuredBoundingBox(source),
                    AssetId: MapStructuredAsset(source, assetsByPath),
                    SourceLocatorJson: locator,
                    Caption: hasAnnotations ? ReadStructuredAnnotation(source, "captions") : null,
                    Footnote: hasAnnotations ? ReadStructuredAnnotation(source, "footnotes") : null));
            }
        }

        return (pages, blocks);
    }

    private static bool HasPhysicalPages(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var suffix = GetString(metadata, "file_suffix")?.Trim().TrimStart('.').ToLowerInvariant();
        return suffix is "pdf" or "ofd"
            || metadata.TryGetProperty("document", out var document)
            && document.ValueKind == JsonValueKind.Object
            && GetString(document, "page_count_kind") == "physical";
    }

    private static string? ReadStructuredAnnotation(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => GetString(item, "content"))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        return entries.Length == 0 ? null : string.Join('\n', entries);
    }

    private static NormalizedBoundingBox? MapStructuredBoundingBox(JsonElement source)
    {
        if (!source.TryGetProperty("bbox", out var box) || box.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = box.EnumerateArray().ToArray();
        if (values.Length != 4 || values.Any(value => value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1))
        {
            return null;
        }

        var result = new NormalizedBoundingBox(
            values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble(), values[3].GetDouble());
        return result.X0 <= result.X1 && result.Y0 <= result.Y1 ? result : null;
    }

    private static Guid? MapStructuredAsset(JsonElement source, IReadOnlyDictionary<string, Guid> assetsByPath)
    {
        var path = GetString(source, "image_source");
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return null;
        }

        return assetsByPath.TryGetValue(path.Normalize(NormalizationForm.FormC), out var assetId) ? assetId : null;
    }

    private static ProviderResultNormalizationException InvalidStructuredContent() =>
        Failure("mineru-result-structured-content-invalid", "The MinerU structured-content artifact has an invalid structure.");
}
