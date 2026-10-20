using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

namespace StructaDoc.Adapters.ProviderResults;

public sealed partial class MinerUResultNormalizer(
    IFileStorage fileStorage,
    ProviderResultNormalizationOptions options,
    ILogger<MinerUResultNormalizer> logger) : IProviderResultNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Content fields in lookup order: the first present, non-null field supplies Block content.
    private static readonly string[] ContentProperties =
        ["text", "content", "table_body", "code_body", "list_items", "body"];

    /// <summary>
    /// The top-level content-list item fields this normalizer reads. A field that is neither in this
    /// set nor deliberately ignored would be dropped silently, so tests compare MinerU fixtures with it.
    /// </summary>
    public static IReadOnlySet<string> RecognizedContentListFields { get; } = new HashSet<string>(
        [
            "type",
            "sub_type",
            "text_level",
            "text_format",
            .. ContentProperties,
            "page_idx",
            "page_id",
            "bbox",
            "score",
            "img_path",
            "image_caption",
            "image_footnote",
            "img_caption",
            "img_footnote",
            "chart_caption",
            "chart_footnote",
            "table_caption",
            "table_footnote",
            "code_caption",
            "code_footnote",
        ],
        StringComparer.Ordinal);

    public bool Supports(string providerType) =>
        providerType is ProviderTypes.MinerUCloud or ProviderTypes.MinerULocal;

    public async Task<ParseBundle> NormalizeAsync(
        ProviderResultNormalizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        options.Validate();

        try
        {
            await using var session = await ProviderArchiveReadSession.OpenAsync(
                fileStorage,
                request.Archive,
                options.TemporaryPath,
                cancellationToken);

            var contentListEntry = FindOptionalJson(
                session,
                "content_list.json",
                "_content_list.json");
            var contentListV2Entry = FindOptionalJson(
                session,
                "content_list_v2.json",
                "_content_list_v2.json");
            // A legacy Content List always owns normalization when both families are present.
            var structuredEntry = contentListEntry is null
                ? FindOptionalJson(session, "structured_content.json")
                : null;
            var markdownEntry = structuredEntry is not null
                && session.Entries.TryGetValue("markdown.md", out var v4Markdown)
                && !IsDirectory(v4Markdown)
                    ? v4Markdown
                    : FindRequiredMarkdown(session);
            var modelEntry = structuredEntry is null
                ? FindOptionalJson(session, "model.json", "_model.json")
                : FindOptionalJson(session, "model_output.json");
            var layoutEntry = structuredEntry is null
                ? FindOptionalJson(session, "layout.json", "_layout.json", "_middle.json")
                : FindOptionalJson(session, "middle_json.json");

            var markdownBytes = await ReadEntryAsync(
                markdownEntry,
                options.MaxMarkdownBytes,
                "mineru-result-markdown-too-large",
                cancellationToken);
            var markdown = DecodeUtf8(markdownBytes, "mineru-result-markdown-invalid");
            if (string.IsNullOrWhiteSpace(markdown))
            {
                throw Failure(
                    "mineru-result-markdown-empty",
                    "The MinerU result Markdown is empty.");
            }

            byte[]? contentListBytes = null;
            JsonDocument? contentList = null;
            JsonDocument? structuredContent = null;
            try
            {
                if (contentListEntry is not null)
                {
                    contentListBytes = await ReadEntryAsync(
                        contentListEntry,
                        options.MaxJsonBytes,
                        "mineru-result-json-too-large",
                        cancellationToken);
                    contentList = ParseJson(
                        contentListBytes,
                        "content_list.json",
                        requireArray: true);
                }

                byte[]? structuredBytes = null;
                if (structuredEntry is not null)
                {
                    structuredBytes = await ReadEntryAsync(
                        structuredEntry,
                        options.MaxJsonBytes,
                        "mineru-result-json-too-large",
                        cancellationToken);
                    structuredContent = ParseJson(structuredBytes, "structured-content.json", requireArray: false);
                }

                // Every deterministic derived key is planned from the validated archive and
                // persisted as an internal manifest before the first derived write. A validation
                // failure, a partial write, or cancellation after this point leaves an inventory
                // that deletion can read even though no canonical row was ever committed.
                var assetPlan = await PlanAssetsAsync(session, request.ParseRunId, cancellationToken);
                var plannedOutputs = new List<string>(MaxArtifactOutputs + assetPlan.Assets.Count)
                {
                    ArtifactStorageKey(request.ParseRunId, "markdown.md"),
                };
                if (contentListEntry is not null)
                {
                    plannedOutputs.Add(ArtifactStorageKey(request.ParseRunId, "content-list.json"));
                }

                if (structuredEntry is not null)
                {
                    plannedOutputs.Add(ArtifactStorageKey(request.ParseRunId, "structured-content.json"));
                }

                if (contentListV2Entry is not null)
                {
                    plannedOutputs.Add(ArtifactStorageKey(request.ParseRunId, "content-list-v2.json"));
                }

                if (layoutEntry is not null)
                {
                    plannedOutputs.Add(ArtifactStorageKey(request.ParseRunId, "layout.json"));
                }

                if (modelEntry is not null)
                {
                    plannedOutputs.Add(ArtifactStorageKey(request.ParseRunId, "model-output.json"));
                }

                plannedOutputs.AddRange(assetPlan.Assets.Select(asset => asset.StorageRef));
                var manifestJson = DerivedOutputManifest.Serialize(
                    request.ParseRunId,
                    request.Archive.Sha256.ToLowerInvariant(),
                    plannedOutputs);
                await using (var manifestContent = new MemoryStream(
                        Encoding.UTF8.GetBytes(manifestJson),
                        writable: false))
                {
                    // A failed or conflicting manifest write fails normalization before any
                    // derived object exists, so a durable manifest always covers every write.
                    await fileStorage.WriteAsync(
                        DerivedOutputManifest.StorageKey(request.ParseRunId),
                        manifestContent,
                        DerivedOutputManifest.MaxJsonBytes,
                        cancellationToken);
                }

                var artifacts = new List<ParseArtifact>
                {
                    CreateArchiveArtifact(request),
                    await StoreArtifactAsync(
                        request.ParseRunId,
                        ArtifactTypes.Markdown,
                        "document.md",
                        "text/markdown",
                        "markdown.md",
                        markdownBytes,
                        options.MaxMarkdownBytes,
                        cancellationToken),
                };

                if (contentListBytes is not null)
                {
                    artifacts.Add(await StoreArtifactAsync(
                        request.ParseRunId,
                        ArtifactTypes.ContentList,
                        "content-list.json",
                        "application/json",
                        "content-list.json",
                        contentListBytes,
                        options.MaxJsonBytes,
                        cancellationToken));
                }

                if (structuredBytes is not null)
                {
                    artifacts.Add(await StoreArtifactAsync(
                        request.ParseRunId,
                        ArtifactTypes.ProviderRaw,
                        "structured-content.json",
                        "application/json",
                        "structured-content.json",
                        structuredBytes,
                        options.MaxJsonBytes,
                        cancellationToken));
                }

                await AddOptionalJsonArtifactAsync(
                    artifacts,
                    request.ParseRunId,
                    contentListV2Entry,
                    ArtifactTypes.ContentList,
                    "content-list-v2.json",
                    "content-list-v2.json",
                    cancellationToken);
                await AddOptionalJsonArtifactAsync(
                    artifacts,
                    request.ParseRunId,
                    layoutEntry,
                    ArtifactTypes.Layout,
                    "layout.json",
                    "layout.json",
                    cancellationToken);
                await AddOptionalJsonArtifactAsync(
                    artifacts,
                    request.ParseRunId,
                    modelEntry,
                    ArtifactTypes.ModelOutput,
                    structuredEntry is null ? "model.json" : "model-output.json",
                    "model-output.json",
                    cancellationToken);

                var (assets, assetsByEntryPath) = await StoreAssetsAsync(
                    assetPlan,
                    cancellationToken);
                var blocks = contentList is null
                    ? []
                    : CreateBlocks(request.ParseRunId, contentList.RootElement, assetsByEntryPath);
                if (contentList is not null)
                {
                    WarnWhenMostItemsLackPages(request, contentList.RootElement);
                }

                var pages = CreatePages(blocks);
                if (structuredContent is not null)
                {
                    (pages, blocks) = CreateStructuredContent(
                        request.ParseRunId,
                        structuredContent.RootElement,
                        assetsByEntryPath,
                        cancellationToken);
                }

                var bundle = new ParseBundle(
                    ParseBundleValidator.CurrentSchemaVersion,
                    request.ParseRunId,
                    pages,
                    blocks,
                    assets,
                    artifacts,
                    CreateProviderMetadata(request));
                var validation = ParseBundleValidator.Validate(bundle);
                if (!validation.IsValid)
                {
                    throw Failure(
                        "mineru-result-bundle-invalid",
                        $"The normalized MinerU result is invalid: {validation.ErrorCode}.");
                }

                return bundle;
            }
            finally
            {
                contentList?.Dispose();
                structuredContent?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderResultNormalizationException)
        {
            throw;
        }
        catch (StorageObjectConflictException exception)
        {
            throw Failure(
                "provider-result-derived-storage-conflict",
                "A derived Provider result conflicts with an existing stored object.",
                ProviderFailureCategory.Permanent,
                exception);
        }
        catch (FileSizeLimitExceededException exception)
        {
            throw Failure(
                "provider-result-derived-size-limit",
                "A derived Provider result exceeds its configured storage limit.",
                ProviderFailureCategory.Permanent,
                exception);
        }
        catch (InvalidDataException exception)
        {
            throw Failure(
                "provider-result-archive-invalid",
                "The stored Provider result is no longer a readable ZIP archive.",
                ProviderFailureCategory.Security,
                exception);
        }
        catch (IOException exception)
        {
            throw Failure(
                "provider-result-normalization-io-failed",
                "Provider result normalization failed while accessing storage.",
                ProviderFailureCategory.Transient,
                exception);
        }
    }

    private static void ValidateRequest(ProviderResultNormalizationRequest request)
    {
        if (request.ParseRunId == Guid.Empty)
        {
            throw new ArgumentException("A Parse Run ID is required.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderType);
        ArgumentNullException.ThrowIfNull(request.Archive);

        if (request.Archive.SizeBytes <= 0
            || request.Archive.Entries is null
            || string.IsNullOrWhiteSpace(request.Archive.Name)
            || string.IsNullOrWhiteSpace(request.Archive.MediaType)
            || string.IsNullOrWhiteSpace(request.Archive.StorageRef)
            || request.Archive.Sha256 is null
            || request.Archive.Sha256.Length != 64)
        {
            throw new ArgumentException(
                "A validated stored Provider archive is required.",
                nameof(request));
        }

        if (request.ProviderType is not ProviderTypes.MinerUCloud
            and not ProviderTypes.MinerULocal)
        {
            throw Failure(
                "provider-result-normalizer-unsupported",
                "No MinerU result normalizer supports the selected Provider type.");
        }
    }

    private static ZipArchiveEntry FindRequiredMarkdown(ProviderArchiveReadSession session)
    {
        if (session.Entries.TryGetValue("full.md", out var exact) && !IsDirectory(exact))
        {
            return exact;
        }

        var matches = session.Entries
            .Where(item =>
                !IsDirectory(item.Value)
                && item.Key.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            > 1 => throw Failure(
                "mineru-result-entry-ambiguous",
                "The MinerU result archive contains ambiguous Markdown artifacts."),
            _ => throw Failure(
                "mineru-result-markdown-missing",
                "The MinerU result archive does not contain a supported Markdown artifact."),
        };
    }

    private static ZipArchiveEntry? FindOptionalJson(
        ProviderArchiveReadSession session,
        string exactPath,
        params string[] suffixes)
    {
        if (session.Entries.TryGetValue(exactPath, out var exact) && !IsDirectory(exact))
        {
            return exact;
        }

        var suffixMatches = session.Entries
            .Where(item =>
                !IsDirectory(item.Value)
                && suffixes.Any(suffix =>
                    item.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Value)
            .ToArray();
        if (suffixMatches.Length > 1)
        {
            throw Failure(
                "mineru-result-entry-ambiguous",
                "The MinerU result archive contains ambiguous JSON artifacts.");
        }

        if (suffixMatches.Length == 1)
        {
            return suffixMatches[0];
        }

        var nestedMatches = session.Entries
            .Where(item =>
                !IsDirectory(item.Value)
                && item.Key.EndsWith($"/{exactPath}", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .ToArray();
        return nestedMatches.Length switch
        {
            0 => null,
            1 => nestedMatches[0],
            _ => throw Failure(
                "mineru-result-entry-ambiguous",
                "The MinerU result archive contains ambiguous JSON artifacts."),
        };
    }

    private async Task AddOptionalJsonArtifactAsync(
        List<ParseArtifact> artifacts,
        Guid parseRunId,
        ZipArchiveEntry? entry,
        string artifactType,
        string artifactName,
        string storedName,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return;
        }

        var bytes = await ReadEntryAsync(
            entry,
            options.MaxJsonBytes,
            "mineru-result-json-too-large",
            cancellationToken);
        using var parsed = ParseJson(bytes, artifactName, requireArray: false);
        artifacts.Add(await StoreArtifactAsync(
            parseRunId,
            artifactType,
            artifactName,
            "application/json",
            storedName,
            bytes,
            options.MaxJsonBytes,
            cancellationToken));
    }

    // The normalizer writes at most this many distinct derived artifacts, which bounds the
    // manifest's artifact section alongside the asset limit that bounds the rest.
    private const int MaxArtifactOutputs = 6;

    private sealed record PlannedAsset(
        string Path,
        ZipArchiveEntry Entry,
        Guid Id,
        string MediaType,
        string Extension,
        string StorageRef);

    private sealed record AssetPlan(
        IReadOnlyList<PlannedAsset> Assets,
        Dictionary<string, Guid> AssetsByPath);

    // Planning runs before the first derived write, so the count and size limits and the image
    // signature detection that decide each asset key all execute while nothing is stored yet.
    private async Task<AssetPlan> PlanAssetsAsync(
        ProviderArchiveReadSession session,
        Guid parseRunId,
        CancellationToken cancellationToken)
    {
        var entries = session.Entries
            .Where(item =>
                !IsDirectory(item.Value)
                && IsImageEntryPath(item.Key))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length > ParseBundleValidator.MaxAssets)
        {
            throw Failure(
                "mineru-result-asset-limit",
                "The MinerU result exceeds the supported image count.");
        }

        var planned = new List<PlannedAsset>(entries.Length);
        var assetsByPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in entries)
        {
            if (entry.Length <= 0 || entry.Length > options.MaxAssetBytes)
            {
                throw Failure(
                    "mineru-result-asset-size-limit",
                    "A MinerU result image exceeds the configured size limit.");
            }

            var mediaType = await DetectImageMediaTypeAsync(entry, cancellationToken);
            var extension = GetCanonicalExtension(mediaType);
            var pathHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(path.Normalize(NormalizationForm.FormC))))
                .ToLowerInvariant()[..24];
            planned.Add(new PlannedAsset(
                path,
                entry,
                CreateDeterministicId(parseRunId, $"asset:{path.ToLowerInvariant()}"),
                mediaType,
                extension,
                $"parse-runs/{parseRunId:N}/assets/{pathHash}{extension}"));
            AddAssetPath(assetsByPath, path, planned[^1].Id);
        }

        return new AssetPlan(planned, assetsByPath);
    }

    private async Task<(IReadOnlyList<ParseAsset> Assets, Dictionary<string, Guid> AssetsByPath)>
        StoreAssetsAsync(AssetPlan plan, CancellationToken cancellationToken)
    {
        var assets = new List<ParseAsset>(plan.Assets.Count);
        foreach (var asset in plan.Assets)
        {
            await using var entryContent = asset.Entry.Open();
            var stored = await fileStorage.WriteAsync(
                asset.StorageRef,
                entryContent,
                options.MaxAssetBytes,
                cancellationToken);
            if (stored.SizeBytes != asset.Entry.Length)
            {
                throw Failure(
                    "mineru-result-entry-size-mismatch",
                    "A MinerU result image does not match its validated size.",
                    ProviderFailureCategory.Security);
            }

            assets.Add(new ParseAsset(
                asset.Id,
                GetDisplayName(asset.Path, asset.Extension),
                asset.MediaType,
                stored.SizeBytes,
                stored.Sha256,
                stored.StorageRef));
        }

        return (assets, plan.AssetsByPath);
    }

    private static bool IsImageEntryPath(string path) =>
        path.StartsWith("images/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/images/", StringComparison.OrdinalIgnoreCase);

    private static void AddAssetPath(
        Dictionary<string, Guid> assetsByPath,
        string path,
        Guid id)
    {
        if (assetsByPath.TryGetValue(path, out var existingPathId) && existingPathId != id)
        {
            throw Failure(
                "mineru-result-entry-ambiguous",
                "The MinerU result archive contains ambiguous image paths.");
        }

        assetsByPath[path] = id;
        var markerIndex = path.LastIndexOf("/images/", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return;
        }

        var relativePath = path[(markerIndex + 1)..];
        if (assetsByPath.TryGetValue(relativePath, out var existingId) && existingId != id)
        {
            throw Failure(
                "mineru-result-entry-ambiguous",
                "The MinerU result archive contains ambiguous image paths.");
        }

        assetsByPath[relativePath] = id;
    }

    private static IReadOnlyList<ParseBlock> CreateBlocks(
        Guid parseRunId,
        JsonElement contentList,
        IReadOnlyDictionary<string, Guid> assetsByPath)
    {
        var sourceBlocks = contentList.EnumerateArray().ToArray();
        if (sourceBlocks.Length > ParseBundleValidator.MaxBlocks)
        {
            throw Failure(
                "mineru-result-block-limit",
                "The MinerU result exceeds the supported Block count.");
        }

        var blocks = new List<ParseBlock>(sourceBlocks.Length);
        for (var index = 0; index < sourceBlocks.Length; index++)
        {
            var source = sourceBlocks[index];
            if (source.ValueKind != JsonValueKind.Object)
            {
                throw Failure(
                    "mineru-result-block-invalid",
                    "A MinerU content-list item is not an object.");
            }

            var providerType = GetString(source, "type");
            if (string.IsNullOrWhiteSpace(providerType))
            {
                throw Failure(
                    "mineru-result-block-invalid",
                    "A MinerU content-list item has no type.");
            }

            var textLevel = GetOptionalInt32(source, "text_level");
            var type = MapBlockType(providerType, textLevel);
            var subtype = MapSubtype(source, providerType, type, textLevel);
            var (content, sourceProperty) = ExtractContent(source);
            var contentFormat = MapContentFormat(source, type, sourceProperty, content);
            var pageNumber = MapPageNumber(source);
            var assetId = MapAsset(source, assetsByPath);
            var (caption, footnote) = MapAnnotations(source, providerType);

            blocks.Add(new ParseBlock(
                CreateDeterministicId(parseRunId, $"block:{index}"),
                index,
                pageNumber,
                type,
                subtype,
                content,
                contentFormat,
                MapBoundingBox(source),
                MapConfidence(source),
                assetId,
                Caption: caption,
                Footnote: footnote));
        }

        return blocks;
    }

    // The MinerU item type alone chooses which caption and footnote fields belong to the Block.
    // Fields of another type are not read; they stay in the retained content-list Artifact.
    private static (string? Caption, string? Footnote) MapAnnotations(JsonElement source, string providerType) =>
        NormalizeToken(providerType) switch
        {
            "image" or "figure" => (
                ReadAnnotation(source, "image_caption", "img_caption"),
                ReadAnnotation(source, "image_footnote", "img_footnote")),
            "chart" => (ReadAnnotation(source, "chart_caption"), ReadAnnotation(source, "chart_footnote")),
            "table" => (ReadAnnotation(source, "table_caption"), ReadAnnotation(source, "table_footnote")),
            "code" or "algorithm" => (ReadAnnotation(source, "code_caption"), ReadAnnotation(source, "code_footnote")),
            _ => (null, null),
        };

    // MinerU writes each caption or footnote as an array of inline text strings. The non-blank
    // string entries are kept in order, unmodified, and joined with a newline; a single string is
    // accepted too. Any other value gives null and never fails normalization. As with page_idx and
    // page_id, a present current field name alone decides, so the legacy name is read only when the
    // current one is absent.
    private static string? ReadAnnotation(JsonElement source, string propertyName, string? legacyPropertyName = null)
    {
        if (!source.TryGetProperty(propertyName, out var value)
            && (legacyPropertyName is null || !source.TryGetProperty(legacyPropertyName, out value)))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            case JsonValueKind.Array:
                var entries = value.EnumerateArray()
                    .Where(entry => entry.ValueKind == JsonValueKind.String)
                    .Select(entry => entry.GetString())
                    .Where(entry => !string.IsNullOrWhiteSpace(entry))
                    .ToArray();
                return entries.Length == 0 ? null : string.Join('\n', entries);
            default:
                return null;
        }
    }

    private static IReadOnlyList<ParsePage> CreatePages(IReadOnlyList<ParseBlock> blocks) =>
        blocks
            .Where(block => block.PageNumber.HasValue)
            .Select(block => block.PageNumber!.Value)
            .Distinct()
            .Order()
            .Select(number => new ParsePage(
                number,
                SourceLocatorJson: JsonSerializer.Serialize(new { providerPageId = number - 1 })))
            .ToArray();

    private static string MapBlockType(string providerType, int? textLevel)
    {
        var normalized = NormalizeToken(providerType);
        return normalized switch
        {
            "text" when textLevel > 0 => "title",
            "title" or "heading" => "title",
            "text" or "paragraph" => "text",
            "list" or "list-item" => "list",
            "table" => "table",
            "equation" or "formula" or "inline-equation" or "interline-equation" => "formula",
            "image" or "figure" or "chart" => "image",
            "code" or "algorithm" => "code",
            "header" or "page-header" => "header",
            "footer" or "page-footer" => "footer",
            "footnote" or "page-footnote" => "footnote",
            "page-number" => "page-number",
            _ => "unknown",
        };
    }

    private static string? MapSubtype(
        JsonElement source,
        string providerType,
        string canonicalType,
        int? textLevel)
    {
        if (canonicalType == "title" && textLevel > 0)
        {
            return $"heading-{Math.Min(textLevel.Value, 99)}";
        }

        // A chart keeps its own subtype even when MinerU adds a sub_type, so it stays
        // distinguishable from a photo or diagram among the image Blocks.
        if (NormalizeToken(providerType) == "chart")
        {
            return "chart";
        }

        var subType = NormalizeOptionalToken(GetString(source, "sub_type"));
        if (subType is not null)
        {
            return subType;
        }

        var normalizedProviderType = NormalizeOptionalToken(providerType);
        return canonicalType == "unknown" ? normalizedProviderType : null;
    }

    private static (string? Content, string? SourceProperty) ExtractContent(JsonElement source)
    {
        foreach (var propertyName in ContentProperties)
        {
            if (!source.TryGetProperty(propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (propertyName == "list_items" && TryJoinStrings(value, out var items))
            {
                return (items, propertyName);
            }

            return value.ValueKind == JsonValueKind.String
                ? (value.GetString(), propertyName)
                : (value.GetRawText(), propertyName);
        }

        return (null, null);
    }

    // MinerU writes list entries as an array of strings; anything else keeps the compact JSON text
    // that other non-string content gets, so no value is lost.
    private static bool TryJoinStrings(JsonElement value, out string? joined)
    {
        joined = null;
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            return false;
        }

        joined = string.Join('\n', value.EnumerateArray().Select(item => item.GetString()));
        return true;
    }

    private static string? MapContentFormat(
        JsonElement source,
        string blockType,
        string? sourceProperty,
        string? content)
    {
        var providerFormat = NormalizeOptionalToken(GetString(source, "text_format"));
        if (providerFormat is not null and not "none")
        {
            return providerFormat;
        }

        if (blockType == "formula")
        {
            return "latex";
        }

        if (sourceProperty is "table_body" or "body"
            && content?.TrimStart().StartsWith('<') == true)
        {
            return "html";
        }

        return content is null ? null : "plain";
    }

    // MinerU writes the zero-based page as page_idx; page_id is an older spelling. When page_idx is
    // present it alone decides the page, so an invalid page_idx is never masked by a valid page_id.
    private static int? MapPageNumber(JsonElement source)
    {
        if (!source.TryGetProperty("page_idx", out var page)
            && !source.TryGetProperty("page_id", out page))
        {
            return null;
        }

        if (page.ValueKind != JsonValueKind.Number
            || !page.TryGetInt32(out var providerPageId)
            || providerPageId < 0
            || providerPageId == int.MaxValue)
        {
            throw Failure(
                "mineru-result-page-invalid",
                "A MinerU Block has an invalid page identifier.");
        }

        return providerPageId + 1;
    }

    private static bool HasPageField(JsonElement source) =>
        source.ValueKind == JsonValueKind.Object
        && (source.TryGetProperty("page_idx", out _) || source.TryGetProperty("page_id", out _));

    // MinerU writes a page on every item, so a content list where most items have none is a layout
    // this normalizer does not recognize. The log carries counts only: no content, paths, or values.
    private void WarnWhenMostItemsLackPages(
        ProviderResultNormalizationRequest request,
        JsonElement contentList)
    {
        var itemCount = contentList.GetArrayLength();
        if (itemCount == 0)
        {
            return;
        }

        var itemsWithoutPage = contentList.EnumerateArray().Count(item => !HasPageField(item));
        if (itemsWithoutPage * 2 <= itemCount)
        {
            return;
        }

        logger.LogWarning(
            "MinerU content list for Parse Run {ParseRunId} from {ProviderType} has {ItemsWithoutPage} of {ItemCount} items without a page field; their Blocks have no page number.",
            request.ParseRunId,
            request.ProviderType,
            itemsWithoutPage,
            itemCount);
    }

    private static NormalizedBoundingBox? MapBoundingBox(JsonElement source)
    {
        if (!source.TryGetProperty("bbox", out var box)
            || box.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = box.EnumerateArray().ToArray();
        if (values.Length != 4
            || values.Any(value =>
                value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number)
                || !double.IsFinite(number)))
        {
            return null;
        }

        var coordinates = values.Select(value => value.GetDouble()).ToArray();
        var scale = coordinates.All(value => value is >= 0 and <= 1)
            ? 1D
            : coordinates.All(value => value is >= 0 and <= 1000)
                ? 1000D
                : 0D;
        if (scale == 0)
        {
            return null;
        }

        var normalized = new NormalizedBoundingBox(
            coordinates[0] / scale,
            coordinates[1] / scale,
            coordinates[2] / scale,
            coordinates[3] / scale);
        return normalized.X0 <= normalized.X1 && normalized.Y0 <= normalized.Y1
            ? normalized
            : null;
    }

    private static double? MapConfidence(JsonElement source)
    {
        if (!source.TryGetProperty("score", out var score)
            || score.ValueKind != JsonValueKind.Number
            || !score.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value is < 0 or > 1)
        {
            return null;
        }

        return value;
    }

    private static Guid? MapAsset(
        JsonElement source,
        IReadOnlyDictionary<string, Guid> assetsByPath)
    {
        var imagePath = GetString(source, "img_path");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var normalizedPath = imagePath.Normalize(NormalizationForm.FormC);
        return assetsByPath.TryGetValue(normalizedPath, out var assetId)
            ? assetId
            : null;
    }

    private async Task<ParseArtifact> StoreArtifactAsync(
        Guid parseRunId,
        string type,
        string name,
        string mediaType,
        string storedName,
        byte[] content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream(content, writable: false);
        var stored = await fileStorage.WriteAsync(
            ArtifactStorageKey(parseRunId, storedName),
            input,
            maxBytes,
            cancellationToken);
        return new ParseArtifact(
            CreateDeterministicId(parseRunId, $"artifact:{type}:{name}"),
            type,
            name,
            mediaType,
            stored.SizeBytes,
            stored.Sha256,
            stored.StorageRef);
    }

    private static string ArtifactStorageKey(Guid parseRunId, string storedName) =>
        $"parse-runs/{parseRunId:N}/artifacts/{storedName}";

    private static ParseArtifact CreateArchiveArtifact(
        ProviderResultNormalizationRequest request) =>
        new(
            CreateDeterministicId(
                request.ParseRunId,
                $"artifact:{ArtifactTypes.ProviderArchive}:{request.Archive.Name}"),
            ArtifactTypes.ProviderArchive,
            request.Archive.Name,
            request.Archive.MediaType,
            request.Archive.SizeBytes,
            request.Archive.Sha256.ToLowerInvariant(),
            request.Archive.StorageRef);

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        string limitCode,
        CancellationToken cancellationToken)
    {
        if (entry.Length <= 0)
        {
            return [];
        }

        if (entry.Length > maxBytes || entry.Length > int.MaxValue)
        {
            throw Failure(limitCode, "A MinerU result artifact exceeds its configured size limit.");
        }

        var content = new byte[entry.Length];
        await using var stream = entry.Open();
        await stream.ReadExactlyAsync(content, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw Failure(
                "mineru-result-entry-size-mismatch",
                "A MinerU result artifact does not match its validated size.",
                ProviderFailureCategory.Security);
        }

        return content;
    }

    private static JsonDocument ParseJson(
        byte[] content,
        string displayName,
        bool requireArray)
    {
        try
        {
            var bytes = RemoveUtf8Bom(content);
            var document = JsonDocument.Parse(bytes.ToArray());
            if (requireArray && document.RootElement.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                throw Failure(
                    "mineru-result-content-list-invalid",
                    "The MinerU content-list artifact is not a JSON array.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw Failure(
                "mineru-result-json-invalid",
                $"The MinerU {displayName} artifact is not valid JSON.",
                ProviderFailureCategory.Permanent,
                exception);
        }
    }

    private static string DecodeUtf8(byte[] content, string errorCode)
    {
        try
        {
            return StrictUtf8.GetString(RemoveUtf8Bom(content));
        }
        catch (DecoderFallbackException exception)
        {
            throw Failure(
                errorCode,
                "The MinerU result Markdown is not valid UTF-8.",
                ProviderFailureCategory.Permanent,
                exception);
        }
    }

    private static ReadOnlySpan<byte> RemoveUtf8Bom(byte[] content) =>
        content.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? content.AsSpan(Encoding.UTF8.Preamble.Length)
            : content;

    private static async Task<string> DetectImageMediaTypeAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[Math.Min(16, checked((int)entry.Length))];
        await using var stream = entry.Open();
        await stream.ReadExactlyAsync(prefix, cancellationToken);

        if (prefix.AsSpan().StartsWith(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (prefix.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }

        if (prefix.AsSpan().StartsWith("GIF87a"u8)
            || prefix.AsSpan().StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (prefix.Length >= 12
            && prefix.AsSpan().StartsWith("RIFF"u8)
            && prefix.AsSpan(8).StartsWith("WEBP"u8))
        {
            return "image/webp";
        }

        throw Failure(
            "mineru-result-image-unsupported",
            "A MinerU result image has an unsupported or invalid media type.");
    }

    private static string GetCanonicalExtension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => throw new InvalidOperationException("Unsupported image media type."),
    };

    private static string GetDisplayName(string path, string extension)
    {
        var name = path[(path.LastIndexOf('/') + 1)..].Normalize(NormalizationForm.FormC);
        return string.IsNullOrWhiteSpace(name) || name.Length > 512
            ? $"image-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..16]}{extension}"
            : name;
    }

    private static string CreateProviderMetadata(ProviderResultNormalizationRequest request)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["providerType"] = request.ProviderType,
        };
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            metadata["model"] = request.Model;
        }

        if (!string.IsNullOrWhiteSpace(request.Backend))
        {
            metadata["backend"] = request.Backend;
        }

        return JsonSerializer.Serialize(metadata);
    }

    private static Guid CreateDeterministicId(Guid namespaceId, string name)
    {
        var namespaceBytes = new byte[16];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);
        var bytes = SHA256.HashData(input)[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static string? GetString(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetOptionalInt32(JsonElement source, string propertyName) =>
        source.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string NormalizeToken(string value) =>
        value.Trim()
            .ToLowerInvariant()
            .Replace('_', '-');

    private static string? NormalizeOptionalToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = NormalizeToken(value);
        return token.Length <= 100
            && token.All(character =>
                char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character)
                || character is '-' or '_')
            ? token
            : null;
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal);

    private static ProviderResultNormalizationException Failure(
        string code,
        string message,
        ProviderFailureCategory category = ProviderFailureCategory.Permanent,
        Exception? innerException = null) =>
        new(code, message, category, innerException);
}
