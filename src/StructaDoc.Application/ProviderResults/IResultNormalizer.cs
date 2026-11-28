using StructaDoc.Application.Canonical;

namespace StructaDoc.Application.ProviderResults;

public interface IResultNormalizer
{
    bool Supports(string providerType);

    Task<ParseBundle> NormalizeAsync(
        ResultNormalizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ResultNormalizationRequest(
    Guid ParseRunId,
    string ProviderType,
    StoredArchive Archive,
    string? Model = null,
    string? Backend = null);
