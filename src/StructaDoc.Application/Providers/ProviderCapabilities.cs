using System.Collections.Frozen;

namespace StructaDoc.Application.Providers;

public sealed class ProviderCapabilities
{
    private readonly FrozenSet<string> supportedMediaTypes;

    public ProviderCapabilities(
        IEnumerable<string> supportedMediaTypes,
        long? maxFileBytes,
        int? maxPages,
        bool supportsCancellation)
    {
        ArgumentNullException.ThrowIfNull(supportedMediaTypes);
        this.supportedMediaTypes = supportedMediaTypes
            .Select(NormalizeMediaType)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        if (this.supportedMediaTypes.Count == 0)
        {
            throw new ArgumentException(
                "At least one supported media type is required.",
                nameof(supportedMediaTypes));
        }

        if (maxFileBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        }

        if (maxPages is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages));
        }

        MaxFileBytes = maxFileBytes;
        MaxPages = maxPages;
        SupportsCancellation = supportsCancellation;
    }

    public IReadOnlySet<string> SupportedMediaTypes => supportedMediaTypes;

    public long? MaxFileBytes { get; }

    public int? MaxPages { get; }

    public bool SupportsCancellation { get; }

    public bool SupportsMediaType(string mediaType) =>
        supportedMediaTypes.Contains(NormalizeMediaType(mediaType));

    private static string NormalizeMediaType(string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        var separatorIndex = mediaType.IndexOf(';', StringComparison.Ordinal);
        var normalized = (separatorIndex < 0 ? mediaType : mediaType[..separatorIndex])
            .Trim()
            .ToLowerInvariant();

        if (!normalized.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("The media type is invalid.", nameof(mediaType));
        }

        return normalized;
    }
}
