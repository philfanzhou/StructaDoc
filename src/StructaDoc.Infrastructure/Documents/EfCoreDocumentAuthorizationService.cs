using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Domain.Resources;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Documents;

public sealed class EfCoreDocumentAuthorizationService(StructaDocDbContext dbContext)
    : IDocumentAuthorizationService
{
    public Task<bool> HasPermissionAsync(
        Guid documentId,
        ResourceAccessContext access,
        DocumentPermissions permission,
        CancellationToken cancellationToken = default)
    {
        if (access.IsAdministrator || access.IsServiceClient)
        {
            return dbContext.Documents.AnyAsync(
                document => document.Id == documentId
                    && document.LifecycleState == ResourceLifecycleStates.Active,
                cancellationToken);
        }

        if (!access.IsInteractiveUser)
        {
            return Task.FromResult(false);
        }

        var required = (int)permission;
        return dbContext.Documents.AnyAsync(
            document => document.Id == documentId
                && document.LifecycleState == ResourceLifecycleStates.Active
                && ((document.OwnerIssuer == access.Issuer
                        && document.OwnerSubject == access.Subject)
                    || document.AccessGrants.Any(grant =>
                        grant.PrincipalIssuer == access.Issuer
                        && grant.PrincipalSubject == access.Subject
                        && (grant.Permissions & required) == required)),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentAccessGrant>> ListGrantsAsync(
        Guid documentId,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default)
    {
        if (!await HasPermissionAsync(documentId, access, DocumentPermissions.Share, cancellationToken))
        {
            return [];
        }

        return await dbContext.DocumentAccessGrants
            .AsNoTracking()
            .Where(grant => grant.DocumentId == documentId)
            .OrderBy(grant => grant.CreatedAtUtc)
            .Select(grant => ToRecord(grant))
            .ToListAsync(cancellationToken);
    }

    public async Task<DocumentAccessGrant?> SetGrantAsync(
        Guid documentId,
        ResourceAccessContext access,
        string issuer,
        string subject,
        DocumentPermissions permissions,
        string actorId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(issuer, subject);
        if (permissions is DocumentPermissions.None || (permissions & ~DocumentPermissions.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }

        if (!await HasPermissionAsync(documentId, access, DocumentPermissions.Share, cancellationToken))
        {
            return null;
        }

        var entity = await dbContext.DocumentAccessGrants.SingleOrDefaultAsync(
            grant => grant.DocumentId == documentId
                && grant.PrincipalIssuer == issuer
                && grant.PrincipalSubject == subject,
            cancellationToken);
        if (entity is null)
        {
            entity = new DocumentAccessGrantEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                PrincipalIssuer = issuer.Trim(),
                PrincipalSubject = subject.Trim(),
                Permissions = (int)permissions,
                CreatedBy = actorId,
                CreatedAtUtc = nowUtc,
            };
            dbContext.DocumentAccessGrants.Add(entity);
        }
        else
        {
            entity.Permissions = (int)permissions;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<bool> RevokeGrantAsync(
        Guid documentId,
        ResourceAccessContext access,
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        if (!await HasPermissionAsync(documentId, access, DocumentPermissions.Share, cancellationToken))
        {
            return false;
        }

        var deleted = await dbContext.DocumentAccessGrants
            .Where(grant => grant.DocumentId == documentId && grant.Id == grantId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1;
    }

    private static DocumentAccessGrant ToRecord(DocumentAccessGrantEntity entity) => new(
        entity.Id,
        entity.DocumentId,
        entity.PrincipalIssuer,
        entity.PrincipalSubject,
        (DocumentPermissions)entity.Permissions,
        entity.CreatedBy,
        entity.CreatedAtUtc);

    private static void ValidateIdentity(string issuer, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (issuer.Trim().Length > 512 || subject.Trim().Length > 255)
        {
            throw new ArgumentException("External identity exceeds its maximum length.");
        }
    }
}
