using StructaDoc.Application.Canonical;

namespace StructaDoc.Application.ProviderResults;

public interface IProviderResultNormalizer
{
    bool Supports(string providerType);

    Task<ParseBundle> NormalizeAsync(
        ProviderResultNormalizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderResultNormalizationRequest(
    Guid ParseRunId,
    string ProviderType,
    StoredProviderArchive Archive,
    string? Model = null,
    string? Backend = null);
