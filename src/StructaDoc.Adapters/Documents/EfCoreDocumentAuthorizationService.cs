using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Adapters.Documents;

public sealed class EfCoreDocumentAuthorizationService(StructaDocDbContext dbContext)
    : IDocumentAuthorizationService
{
    public Task<bool> HasPermissionAsync(
        Guid documentId,
        ResourceAccessContext access,
        DocumentPermissions permission,
        CancellationToken cancellationToken = default)
    {
        if (access.IsAdministrator)
        {
            return dbContext.Documents.AnyAsync(
                document => document.Id == documentId
                    && document.LifecycleState == ResourceLifecycleStates.Active,
                cancellationToken);
        }

        if (!access.HasPrincipalIdentity)
        {
            return Task.FromResult(false);
        }

        var owner = DocumentOwnerIdentity.From(access);
        var required = (int)permission;
        return dbContext.Documents.AnyAsync(
            document => document.Id == documentId
                && document.LifecycleState == ResourceLifecycleStates.Active
                && ((document.OwnerIssuer == owner.Issuer
                        && document.OwnerSubject == owner.Subject)
                    || document.AccessGrants.Any(grant =>
                        grant.PrincipalIssuer == owner.Issuer
                        && grant.PrincipalSubject == owner.Subject
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
        CanonicalActor actor,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateIdentity(issuer, subject);
        var principal = CanonicalActor.Create(issuer, subject);
        var principalIssuer = principal.EncodeIssuer();
        var principalSubject = principal.EncodeSubject();
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
                && grant.PrincipalIssuer == principalIssuer
                && grant.PrincipalSubject == principalSubject,
            cancellationToken);
        if (entity is null)
        {
            entity = new DocumentAccessGrantEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                PrincipalIssuer = principalIssuer,
                PrincipalSubject = principalSubject,
                Permissions = (int)permissions,
                CreatedByIssuer = actor.EncodeIssuer(),
                CreatedBySubject = actor.EncodeSubject(),
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

    private static DocumentAccessGrant ToRecord(DocumentAccessGrantEntity entity)
    {
        var principal = CanonicalActor.FromStoredBytes(
            entity.PrincipalIssuer,
            entity.PrincipalSubject);
        var actorState = CanonicalActorPersistence.ValidateState(
            entity.CreatedByIssuer,
            entity.CreatedBySubject,
            entity.CreatedByLegacy,
            CanonicalActorPersistence.MaximumAccessGrantLegacyByteCount,
            allowEmpty: false);
        var createdBy = actorState == PersistedActorState.Canonical
            ? CanonicalActor.FromStoredBytes(
                entity.CreatedByIssuer!,
                entity.CreatedBySubject!).ToLegacyDisplayString()
            : CanonicalActorPersistence.DecodeLegacy(
                entity.CreatedByLegacy!,
                CanonicalActorPersistence.MaximumAccessGrantLegacyByteCount);
        return new DocumentAccessGrant(
            entity.Id,
            entity.DocumentId,
            principal.Issuer,
            principal.Subject,
            (DocumentPermissions)entity.Permissions,
            createdBy,
            entity.CreatedAtUtc);
    }

    private static void ValidateIdentity(string issuer, string subject)
    {
        if (!PrincipalIdentity.IsValid(issuer, subject))
        {
            throw new ArgumentException("Grant principal is neither an OIDC issuer and subject pair nor an API client.");
        }
    }
}
