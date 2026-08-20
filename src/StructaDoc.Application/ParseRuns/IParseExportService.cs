using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.ParseRuns;

public interface IParseExportService
{
    Task<string?> GetHtmlEntityTagAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default);
    Task<ParseResultContent?> CreateAsync(Guid parseRunId, string format, ResourceAccessContext access, CancellationToken cancellationToken = default);
}
