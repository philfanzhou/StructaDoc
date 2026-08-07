using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.ParseRuns;

public interface IParseExportService
{
    Task<ParseResultContent?> CreateAsync(Guid parseRunId, string format, ResourceAccessContext access, CancellationToken cancellationToken = default);
}
