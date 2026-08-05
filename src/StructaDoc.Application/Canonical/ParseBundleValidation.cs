using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StructaDoc.Application.Canonical;

public sealed record ParseBundleValidationResult(
    bool IsValid,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static ParseBundleValidationResult Valid { get; } = new(true);

    public static ParseBundleValidationResult Invalid(string code, string message) =>
        new(false, code, message);
}

public static class ParseBundleValidator
{
    public const string CurrentSchemaVersion = "1.0";
    public const int MaxPages = 10_000;
    public const int MaxBlocks = 100_000;
    public const int MaxAssets = 10_000;
    public const int MaxArtifacts = 10_000;
    public const int MaxBlockContentCharacters = 4 * 1024 * 1024;
    public const int MaxTotalBlockContentCharacters = 64 * 1024 * 1024;
    public const int MaxProviderDataBytes = 64 * 1024;
    public const int MaxMetadataBytes = 16 * 1024;
    public const int MaxSourceLocatorBytes = 8 * 1024;
    public const int MaxAggregateJsonBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> SensitivePropertyNames = new(
        [
            "authorization",
            "credential",
            "credentials",
            "password",
            "secret",
            "token",
            "apikey",
            "accesstoken",
            "refreshtoken",
            "storageref",
            "internalpath",
            "filepath",
            "path",
            "imgpath",
            "imagepath",
            "outputpath",
        ],
        StringComparer.Ordinal);

    public static ParseBundleValidationResult Validate(ParseBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (!string.Equals(bundle.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Invalid("unsupported-schema-version", "The Parse Bundle schema version is not supported.");
        }

        if (bundle.ParseRunId == Guid.Empty)
        {
            return Invalid("invalid-parse-run-id", "The Parse Bundle requires a Parse Run ID.");
        }

        if (bundle.Pages is null || bundle.Blocks is null || bundle.Assets is null || bundle.Artifacts is null)
        {
            return Invalid("missing-collection", "Parse Bundle collections cannot be null.");
        }

        if (bundle.Pages.Count > MaxPages
            || bundle.Blocks.Count > MaxBlocks
            || bundle.Assets.Count > MaxAssets
            || bundle.Artifacts.Count > MaxArtifacts)
        {
            return Invalid("bundle-limit-exceeded", "The Parse Bundle exceeds a collection limit.");
        }

        var providerMetadata = ValidateJsonObject(
            bundle.ProviderMetadataJson,
            MaxMetadataBytes,
            rejectSensitiveData: true,
            "provider-metadata");
        if (!providerMetadata.IsValid)
        {
            return providerMetadata;
        }

        long aggregateJsonBytes = Encoding.UTF8.GetByteCount(bundle.ProviderMetadataJson);

        var pageNumbers = new HashSet<int>();
        foreach (var page in bundle.Pages)
        {
            if (page is null || page.Number <= 0 || !pageNumbers.Add(page.Number))
            {
                return Invalid("invalid-page", "Page numbers must be positive and unique.");
            }

            if (!IsPositiveFinite(page.Width) || !IsPositiveFinite(page.Height))
            {
                return Invalid("invalid-page-dimensions", "Page dimensions must be positive finite values.");
            }

            if (!IsOptionalToken(page.Unit, 32))
            {
                return Invalid("invalid-page-unit", "The Page unit is invalid.");
            }

            var sourceLocator = ValidateOptionalJsonObject(
                page.SourceLocatorJson,
                MaxSourceLocatorBytes,
                rejectSensitiveData: true,
                "page-source-locator");
            if (!sourceLocator.IsValid)
            {
                return sourceLocator;
            }

            aggregateJsonBytes += GetByteCount(page.SourceLocatorJson);
        }

        var assetIds = new HashSet<Guid>();
        foreach (var asset in bundle.Assets)
        {
            if (asset is null || asset.Id == Guid.Empty || !assetIds.Add(asset.Id))
            {
                return Invalid("invalid-asset-id", "Asset IDs must be non-empty and unique.");
            }

            if (!ValidateResourceName(asset.Name))
            {
                return Invalid("invalid-asset-name", "Asset names must be safe display names.");
            }

            var storedFile = ValidateStoredFile(
                asset.MediaType,
                asset.SizeBytes,
                asset.Sha256,
                asset.StorageRef);
            if (!storedFile.IsValid)
            {
                return storedFile;
            }

            if (asset.Width is <= 0 || asset.Height is <= 0)
            {
                return Invalid("invalid-asset-dimensions", "Asset dimensions must be positive when present.");
            }
        }

        var artifactIds = new HashSet<Guid>();
        var artifactKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in bundle.Artifacts)
        {
            if (artifact is null || artifact.Id == Guid.Empty || !artifactIds.Add(artifact.Id))
            {
                return Invalid("invalid-artifact-id", "Artifact IDs must be non-empty and unique.");
            }

            if (!IsToken(artifact.Type, 64) || !ValidateResourceName(artifact.Name))
            {
                return Invalid("invalid-artifact-key", "Artifact types and names are invalid.");
            }

            if (!artifactKeys.Add($"{artifact.Type}\n{artifact.Name}"))
            {
                return Invalid("duplicate-artifact", "Artifact type and name pairs must be unique.");
            }

            var storedFile = ValidateStoredFile(
                artifact.MediaType,
                artifact.SizeBytes,
                artifact.Sha256,
                artifact.StorageRef);
            if (!storedFile.IsValid)
            {
                return storedFile;
            }

            var metadata = ValidateOptionalJsonObject(
                artifact.MetadataJson,
                MaxMetadataBytes,
                rejectSensitiveData: true,
                "artifact-metadata");
            if (!metadata.IsValid)
            {
                return metadata;
            }

            aggregateJsonBytes += GetByteCount(artifact.MetadataJson);
        }

        var blockIds = new HashSet<Guid>();
        long totalBlockContentCharacters = 0;
        for (var index = 0; index < bundle.Blocks.Count; index++)
        {
            var block = bundle.Blocks[index];
            if (block is null || block.Id == Guid.Empty || !blockIds.Add(block.Id))
            {
                return Invalid("invalid-block-id", "Block IDs must be non-empty and unique.");
            }

            if (block.Sequence != index)
            {
                return Invalid("invalid-block-sequence", "Block sequence must be continuous, ordered, and start at zero.");
            }

            if (block.PageNumber.HasValue && !pageNumbers.Contains(block.PageNumber.Value))
            {
                return Invalid("invalid-block-page", "A Block page number must reference a Page in the same Bundle.");
            }

            if (!IsToken(block.Type, 64)
                || !IsOptionalToken(block.Subtype, 100)
                || !IsOptionalToken(block.ContentFormat, 32))
            {
                return Invalid("invalid-block-type", "Block type, subtype, or content format is invalid.");
            }

            if (block.Content?.Length > MaxBlockContentCharacters)
            {
                return Invalid("block-content-too-large", "A Block content value exceeds the size limit.");
            }

            totalBlockContentCharacters += block.Content?.Length ?? 0;
            if (totalBlockContentCharacters > MaxTotalBlockContentCharacters)
            {
                return Invalid("bundle-content-too-large", "The Parse Bundle content exceeds the aggregate size limit.");
            }

            if (!ValidateBoundingBox(block.BoundingBox))
            {
                return Invalid("invalid-bounding-box", "A Block bounding box must use normalized coordinates.");
            }

            if (!IsUnitInterval(block.Confidence))
            {
                return Invalid("invalid-confidence", "A Block confidence must be between zero and one.");
            }

            if (block.AssetId.HasValue && !assetIds.Contains(block.AssetId.Value))
            {
                return Invalid("invalid-block-asset", "A Block Asset ID must reference an Asset in the same Bundle.");
            }

            var sourceLocator = ValidateOptionalJsonObject(
                block.SourceLocatorJson,
                MaxSourceLocatorBytes,
                rejectSensitiveData: true,
                "block-source-locator");
            if (!sourceLocator.IsValid)
            {
                return sourceLocator;
            }

            var providerData = ValidateOptionalJsonObject(
                block.ProviderDataJson,
                MaxProviderDataBytes,
                rejectSensitiveData: true,
                "block-provider-data");
            if (!providerData.IsValid)
            {
                return providerData;
            }

            aggregateJsonBytes += GetByteCount(block.SourceLocatorJson);
            aggregateJsonBytes += GetByteCount(block.ProviderDataJson);
            if (aggregateJsonBytes > MaxAggregateJsonBytes)
            {
                return Invalid("bundle-json-too-large", "The Parse Bundle JSON extensions exceed the aggregate size limit.");
            }
        }

        return ParseBundleValidationResult.Valid;
    }

    public static string ComputeFingerprint(ParseBundle bundle)
    {
        var validation = Validate(bundle);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage, nameof(bundle));
        }

        using var hash = SHA256.Create();
        using (var hashingStream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
        {
            using (var writer = new Utf8JsonWriter(hashingStream))
            {
                WriteFingerprintPayload(writer, bundle);
                writer.Flush();
            }

            hashingStream.FlushFinalBlock();
        }

        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    private static void WriteFingerprintPayload(Utf8JsonWriter writer, ParseBundle bundle)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", bundle.SchemaVersion);
        writer.WriteString("parseRunId", bundle.ParseRunId.ToString("N"));

        writer.WritePropertyName("pages");
        writer.WriteStartArray();
        foreach (var page in bundle.Pages.OrderBy(page => page.Number))
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", page.Number);
            WriteNullableNumber(writer, "width", page.Width);
            WriteNullableNumber(writer, "height", page.Height);
            WriteNullableString(writer, "unit", page.Unit);
            WriteJsonProperty(writer, "sourceLocator", page.SourceLocatorJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("blocks");
        writer.WriteStartArray();
        foreach (var block in bundle.Blocks.OrderBy(block => block.Sequence))
        {
            writer.WriteStartObject();
            writer.WriteString("id", block.Id.ToString("N"));
            writer.WriteNumber("sequence", block.Sequence);
            WriteNullableNumber(writer, "pageNumber", block.PageNumber);
            writer.WriteString("type", block.Type);
            WriteNullableString(writer, "subtype", block.Subtype);
            WriteNullableString(writer, "content", block.Content);
            WriteNullableString(writer, "contentFormat", block.ContentFormat);
            writer.WritePropertyName("boundingBox");
            if (block.BoundingBox is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteNumber("x0", block.BoundingBox.X0);
                writer.WriteNumber("y0", block.BoundingBox.Y0);
                writer.WriteNumber("x1", block.BoundingBox.X1);
                writer.WriteNumber("y1", block.BoundingBox.Y1);
                writer.WriteEndObject();
            }

            WriteNullableNumber(writer, "confidence", block.Confidence);
            WriteNullableString(writer, "assetId", block.AssetId?.ToString("N"));
            WriteJsonProperty(writer, "sourceLocator", block.SourceLocatorJson);
            WriteJsonProperty(writer, "providerData", block.ProviderDataJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("assets");
        writer.WriteStartArray();
        foreach (var asset in bundle.Assets.OrderBy(asset => asset.Id))
        {
            writer.WriteStartObject();
            writer.WriteString("id", asset.Id.ToString("N"));
            writer.WriteString("name", asset.Name);
            writer.WriteString("mediaType", asset.MediaType);
            writer.WriteNumber("sizeBytes", asset.SizeBytes);
            writer.WriteString("sha256", asset.Sha256);
            writer.WriteString("storageRef", asset.StorageRef);
            WriteNullableNumber(writer, "width", asset.Width);
            WriteNullableNumber(writer, "height", asset.Height);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("artifacts");
        writer.WriteStartArray();
        foreach (var artifact in bundle.Artifacts.OrderBy(artifact => artifact.Id))
        {
            writer.WriteStartObject();
            writer.WriteString("id", artifact.Id.ToString("N"));
            writer.WriteString("type", artifact.Type);
            writer.WriteString("name", artifact.Name);
            writer.WriteString("mediaType", artifact.MediaType);
            writer.WriteNumber("sizeBytes", artifact.SizeBytes);
            writer.WriteString("sha256", artifact.Sha256);
            writer.WriteString("storageRef", artifact.StorageRef);
            WriteJsonProperty(writer, "metadata", artifact.MetadataJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteJsonProperty(writer, "providerMetadata", bundle.ProviderMetadataJson);
        writer.WriteEndObject();
    }

    private static void WriteJsonProperty(Utf8JsonWriter writer, string name, string? json)
    {
        writer.WritePropertyName(name);
        if (json is null)
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(json);
        WriteCanonicalJson(writer, document.RootElement);
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static ParseBundleValidationResult ValidateStoredFile(
        string mediaType,
        long sizeBytes,
        string sha256,
        string storageRef)
    {
        if (!IsMediaType(mediaType))
        {
            return Invalid("invalid-media-type", "A stored resource media type is invalid.");
        }

        if (sizeBytes <= 0)
        {
            return Invalid("invalid-resource-size", "A stored resource size must be positive.");
        }

        if (sha256 is null
            || sha256.Length != 64
            || sha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
        {
            return Invalid("invalid-resource-hash", "A stored resource requires a lowercase SHA-256 hash.");
        }

        if (!IsStorageReference(storageRef))
        {
            return Invalid("invalid-storage-reference", "A stored resource reference is invalid.");
        }

        return ParseBundleValidationResult.Valid;
    }

    private static ParseBundleValidationResult ValidateOptionalJsonObject(
        string? value,
        int maxBytes,
        bool rejectSensitiveData,
        string fieldName) => value is null
            ? ParseBundleValidationResult.Valid
            : ValidateJsonObject(value, maxBytes, rejectSensitiveData, fieldName);

    private static ParseBundleValidationResult ValidateJsonObject(
        string value,
        int maxBytes,
        bool rejectSensitiveData,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            return Invalid("invalid-json", $"The {fieldName} JSON is empty or exceeds its size limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("invalid-json", $"The {fieldName} value must be a JSON object.");
            }

            if (rejectSensitiveData && ContainsSensitiveData(document.RootElement))
            {
                return Invalid("sensitive-provider-data", $"The {fieldName} value contains a sensitive field or URL.");
            }
        }
        catch (JsonException)
        {
            return Invalid("invalid-json", $"The {fieldName} value is not valid JSON.");
        }

        return ParseBundleValidationResult.Valid;
    }

    private static bool ContainsSensitiveData(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var normalizedName = new string(property.Name
                        .Where(char.IsLetterOrDigit)
                        .Select(char.ToLowerInvariant)
                        .ToArray());
                    if (SensitivePropertyNames.Contains(normalizedName)
                        || ContainsSensitiveData(property.Value))
                    {
                        return true;
                    }
                }

                break;

            case JsonValueKind.Array:
                return element.EnumerateArray().Any(ContainsSensitiveData);

            case JsonValueKind.String:
                var value = element.GetString();
                return value is not null
                    && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    && value.Contains('?', StringComparison.Ordinal);
        }

        return false;
    }

    private static bool ValidateBoundingBox(NormalizedBoundingBox? box) =>
        box is null
        || (IsUnitInterval(box.X0)
            && IsUnitInterval(box.Y0)
            && IsUnitInterval(box.X1)
            && IsUnitInterval(box.Y1)
            && box.X0 <= box.X1
            && box.Y0 <= box.Y1);

    private static bool IsPositiveFinite(double? value) =>
        !value.HasValue || (double.IsFinite(value.Value) && value.Value > 0);

    private static bool IsUnitInterval(double? value) =>
        !value.HasValue || IsUnitInterval(value.Value);

    private static bool IsUnitInterval(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool IsOptionalToken(string? value, int maxLength) =>
        value is null || IsToken(value, maxLength);

    private static bool IsToken(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && value.All(character =>
            char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character is '-' or '_');

    private static bool ValidateResourceName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 512
        && value.All(character => !char.IsControl(character))
        && !value.Contains('/', StringComparison.Ordinal)
        && !value.Contains('\\', StringComparison.Ordinal);

    private static bool IsMediaType(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
        {
            return false;
        }

        var parts = value.Split('/');
        return parts.Length == 2
            && parts.All(part =>
                part.Length > 0
                && part.All(character =>
                    char.IsAsciiLetterLower(character)
                    || char.IsAsciiDigit(character)
                    || character is '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-'));
    }

    private static bool IsStorageReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.Contains("\\", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.All(segment =>
            segment.Length > 0
            && segment is not "." and not ".."
            && !segment.Contains(':')
            && segment.All(character => !char.IsControl(character)));
    }

    private static int GetByteCount(string? value) =>
        value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private static ParseBundleValidationResult Invalid(string code, string message) =>
        ParseBundleValidationResult.Invalid(code, message);
}
