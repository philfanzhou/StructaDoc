using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

namespace StructaDoc.Infrastructure.ProviderResults;

public sealed class MinerUResultNormalizer(
    IFileStorage fileStorage,
    ProviderResultNormalizationOptions options) : IProviderResultNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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

            var markdownEntry = FindRequiredMarkdown(session);
            var contentListEntry = FindOptionalJson(
                session,
                "content_list.json",
                "_content_list.json");
            var contentListV2Entry = FindOptionalJson(
                session,
                "content_list_v2.json",
                "_content_list_v2.json");
            var modelEntry = FindOptionalJson(session, "model.json", "_model.json");
            var layoutEntry = FindOptionalJson(
                session,
                "layout.json",
                "_layout.json",
                "_middle.json");

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
                    "model.json",
                    "model-output.json",
                    cancellationToken);

                var (assets, assetsByEntryPath) = await StoreAssetsAsync(
                    session,
                    request.ParseRunId,
                    cancellationToken);
                var blocks = contentList is null
                    ? []
                    : CreateBlocks(request.ParseRunId, contentList.RootElement, assetsByEntryPath);
                var pages = CreatePages(blocks);

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

    private async Task<(IReadOnlyList<ParseAsset> Assets, Dictionary<string, Guid> AssetsByPath)>
        StoreAssetsAsync(
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

        var assets = new List<ParseAsset>(entries.Length);
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
            var storageRef = $"parse-runs/{parseRunId:N}/assets/{pathHash}{extension}";
            await using var entryContent = entry.Open();
            var stored = await fileStorage.WriteAsync(
                storageRef,
                entryContent,
                options.MaxAssetBytes,
                cancellationToken);
            if (stored.SizeBytes != entry.Length)
            {
                throw Failure(
                    "mineru-result-entry-size-mismatch",
                    "A MinerU result image does not match its validated size.",
                    ProviderFailureCategory.Security);
            }

            var id = CreateDeterministicId(parseRunId, $"asset:{path.ToLowerInvariant()}");
            assets.Add(new ParseAsset(
                id,
                GetDisplayName(path, extension),
                mediaType,
                stored.SizeBytes,
                stored.Sha256,
                stored.StorageRef));
            AddAssetPath(assetsByPath, path, id);
        }

        return (assets, assetsByPath);
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
                assetId));
        }

        return blocks;
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
            "image" or "figure" => "image",
            "code" or "algorithm" => "code",
            "header" or "page-header" => "header",
            "footer" or "page-footer" => "footer",
            "footnote" => "footnote",
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
        foreach (var propertyName in new[] { "text", "content", "body" })
        {
            if (!source.TryGetProperty(propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            return value.ValueKind == JsonValueKind.String
                ? (value.GetString(), propertyName)
                : (value.GetRawText(), propertyName);
        }

        return (null, null);
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

        if (sourceProperty == "body"
            && content?.TrimStart().StartsWith('<') == true)
        {
            return "html";
        }

        return content is null ? null : "plain";
    }

    private static int? MapPageNumber(JsonElement source)
    {
        if (!source.TryGetProperty("page_id", out var page))
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
            $"parse-runs/{parseRunId:N}/artifacts/{storedName}",
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
