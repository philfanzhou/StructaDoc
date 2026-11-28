using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.ParseRuns;

public interface IExportService
{
    Task<string?> GetHtmlEntityTagAsync(
        Guid parseRunId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
    Task<ResultContent?> CreateAsync(
        Guid parseRunId,
        string format,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
}
