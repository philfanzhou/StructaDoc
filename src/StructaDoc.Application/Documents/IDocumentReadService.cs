namespace StructaDoc.Application.Documents;

public interface IDocumentReadService
{
    Task<DocumentPage> ListAsync(
        int limit,
        DocumentCursor? cursor = null,
        CancellationToken cancellationToken = default);

    Task<DocumentRecord?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<DocumentContent?> OpenContentAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
