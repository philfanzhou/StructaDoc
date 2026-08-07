namespace StructaDoc.Application.Documents;

using StructaDoc.Application.Authentication;

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

    Task<DocumentPage> ListAccessibleAsync(
        int limit,
        ResourceAccessContext access,
        DocumentCursor? cursor = null,
        string? fileName = null,
        string? parseStatus = null,
        CancellationToken cancellationToken = default);

    Task<DocumentRecord?> GetAccessibleAsync(
        Guid id,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);

    Task<DocumentContent?> OpenAccessibleContentAsync(
        Guid id,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);
}
