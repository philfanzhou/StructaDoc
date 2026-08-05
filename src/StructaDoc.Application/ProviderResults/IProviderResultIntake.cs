using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ProviderResults;

public interface IProviderResultIntake
{
    Task<StoredProviderArchive> StoreArchiveAsync(
        Guid parseRunId,
        ProviderResultContent result,
        CancellationToken cancellationToken = default);

    Task<StoredProviderArchive?> TryLoadArchiveAsync(
        Guid parseRunId,
        CancellationToken cancellationToken = default);
}
