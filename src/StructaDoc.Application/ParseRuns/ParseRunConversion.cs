using System.Text.Json;
using System.Text.Json.Serialization;
using StructaDoc.Application.Canonical;

namespace StructaDoc.Application.ParseRuns;

public sealed record ParseRunConversion(
    string ConverterType,
    string ConverterVersion,
    string SourceMediaType,
    string OutputMediaType,
    Guid ArtifactId,
    string ArtifactName,
    long SizeBytes,
    string Sha256,
    string StorageRef,
    string OutputFormat)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    public ParseArtifact ToArtifact()
    {
        Validate();
        return new ParseArtifact(
            ArtifactId,
            ArtifactTypes.NormalizedPdf,
            ArtifactName,
            OutputMediaType,
            SizeBytes,
            Sha256,
            StorageRef,
            JsonSerializer.Serialize(
                new ConversionArtifactMetadata(
                    ConverterType,
                    ConverterVersion,
                    SourceMediaType,
                    OutputFormat),
                SerializerOptions));
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConverterType);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConverterVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceMediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputMediaType);
        ArgumentOutOfRangeException.ThrowIfEqual(ArtifactId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(ArtifactName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SizeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(Sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(StorageRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputFormat);

        if (ConverterType.Length > 64
            || ConverterType.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.'))
        {
            throw new ArgumentException("Converter type is invalid.", nameof(ConverterType));
        }

        if (ConverterVersion.Length > 128 || ConverterVersion.Any(char.IsControl))
        {
            throw new ArgumentException("Converter version is invalid.", nameof(ConverterVersion));
        }

        ValidateMediaType(SourceMediaType, nameof(SourceMediaType));
        ValidateMediaType(OutputMediaType, nameof(OutputMediaType));

        if (ArtifactName.Length > 255
            || ArtifactName.Any(char.IsControl)
            || ArtifactName.Contains('/', StringComparison.Ordinal)
            || ArtifactName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Conversion Artifact name is invalid.", nameof(ArtifactName));
        }

        if (Sha256.Length != 64
            || Sha256.Any(character => !Uri.IsHexDigit(character))
            || !string.Equals(Sha256, Sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Conversion SHA-256 must be lowercase hexadecimal.", nameof(Sha256));
        }

        ValidateStorageRef(StorageRef);

        if (OutputFormat.Length > 32
            || OutputFormat.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Conversion output format is invalid.", nameof(OutputFormat));
        }
    }

    public static ParseRunConversion FromJson(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new JsonException("The conversion snapshot is empty.");
            }

            var conversion = JsonSerializer.Deserialize<ParseRunConversion>(json, SerializerOptions)
                ?? throw new JsonException("The conversion snapshot is empty.");
            conversion.Validate();
            return conversion;
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The conversion snapshot is invalid.", exception);
        }
    }

    private static void ValidateMediaType(string mediaType, string parameterName)
    {
        if (mediaType.Length > 255
            || mediaType.Any(char.IsControl)
            || mediaType.Contains(';', StringComparison.Ordinal)
            || mediaType.Count(character => character == '/') != 1)
        {
            throw new ArgumentException("Conversion media type is invalid.", parameterName);
        }
    }

    private static void ValidateStorageRef(string storageRef)
    {
        if (storageRef.Length > 2048
            || Path.IsPathRooted(storageRef)
            || storageRef.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Conversion storage reference is invalid.", nameof(StorageRef));
        }

        var segments = storageRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new ArgumentException("Conversion storage reference is invalid.", nameof(StorageRef));
        }
    }

    private sealed record ConversionArtifactMetadata(
        string ConverterType,
        string ConverterVersion,
        string SourceMediaType,
        string OutputFormat);
}
