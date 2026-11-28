using StructaDoc.Application.Providers;

namespace StructaDoc.Application.ProviderResults;

public interface IResultIntake
{
    Task<StoredArchive> StoreArchiveAsync(
        Guid parseRunId,
        ProviderResultContent result,
        CancellationToken cancellationToken = default);

    Task<StoredArchive?> TryLoadArchiveAsync(
        Guid parseRunId,
        CancellationToken cancellationToken = default);
}
