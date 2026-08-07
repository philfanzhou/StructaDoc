using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.Documents;

public interface IDocumentAuthorizationService
{
    Task<bool> HasPermissionAsync(
        Guid documentId,
        ResourceAccessContext access,
        DocumentPermissions permission,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentAccessGrant>> ListGrantsAsync(
        Guid documentId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default);

    Task<DocumentAccessGrant?> SetGrantAsync(
        Guid documentId,
        ResourceAccessContext access,
        string issuer,
        string subject,
        DocumentPermissions permissions,
        string actorId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeGrantAsync(
        Guid documentId,
        ResourceAccessContext access,
        Guid grantId,
        CancellationToken cancellationToken = default);
}

public sealed record DocumentAccessGrant(
    Guid Id,
    Guid DocumentId,
    string Issuer,
    string Subject,
    DocumentPermissions Permissions,
    string CreatedBy,
    DateTime CreatedAtUtc);
