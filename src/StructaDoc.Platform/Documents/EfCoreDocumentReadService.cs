using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Storage;
using StructaDoc.Platform.Persistence;
using StructaDoc.Platform.Persistence.Entities;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Platform.Documents;

public sealed class EfCoreDocumentReadService(
    StructaDocDbContext dbContext,
    IFileStorage fileStorage,
    ILogger<EfCoreDocumentReadService> logger) : IDocumentReadService
{
    public async Task<DocumentPage> ListAsync(
        int limit,
        DocumentCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        return await ListAccessibleAsync(
            limit,
            ResourceAccessContext.System,
            cursor,
            cancellationToken: cancellationToken);
    }

    public async Task<DocumentPage> ListAccessibleAsync(
        int limit,
        ResourceAccessContext access,
        DocumentCursor? cursor = null,
        string? fileName = null,
        string? parseStatus = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var query = ApplyAccess(
            dbContext.Documents.AsNoTracking()
                .Where(document => document.LifecycleState == ResourceLifecycleStates.Active),
            access,
            DocumentPermissions.Read);

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var normalizedFileName = fileName.Trim();
            query = query.Where(document => document.OriginalFileName.Contains(normalizedFileName));
        }

        if (!string.IsNullOrWhiteSpace(parseStatus))
        {
            var normalizedStatus = parseStatus.Trim();
            query = string.Equals(normalizedStatus, "unparsed", StringComparison.OrdinalIgnoreCase)
                ? query.Where(document => !document.ParseRuns.Any(run =>
                    run.LifecycleState == ResourceLifecycleStates.Active))
                : query.Where(document => document.ParseRuns
                    .Where(run => run.LifecycleState == ResourceLifecycleStates.Active)
                    .OrderByDescending(run => run.CreatedAtUtc)
                    .Select(run => run.Status)
                    .FirstOrDefault() == normalizedStatus);
        }

        if (cursor is not null)
        {
            RequireUtc(cursor.CreatedAtUtc);
            query = query.Where(document =>
                document.CreatedAtUtc < cursor.CreatedAtUtc
                || (document.CreatedAtUtc == cursor.CreatedAtUtc
                    && document.Id.CompareTo(cursor.Id) < 0));
        }

        var documents = await query
            .OrderByDescending(document => document.CreatedAtUtc)
            .ThenByDescending(document => document.Id)
            .Select(document => new DocumentRecord(
                document.Id,
                document.OriginalFileName,
                document.MediaType,
                document.Extension,
                document.SizeBytes,
                document.Sha256,
                document.CreatedAtUtc,
                document.ParseRuns
                    .Where(run => run.LifecycleState == ResourceLifecycleStates.Active)
                    .OrderByDescending(run => run.CreatedAtUtc)
                    .Select(run => run.Status)
                    .FirstOrDefault(),
                access.IsInteractiveUser
                    && document.OwnerIssuer == access.Issuer
                    && document.OwnerSubject == access.Subject))
            .Take(checked(limit + 1))
            .ToListAsync(cancellationToken);
        var hasMore = documents.Count > limit;

        if (hasMore)
        {
            documents.RemoveAt(documents.Count - 1);
        }

        var nextCursor = hasMore
            ? new DocumentCursor(documents[^1].CreatedAtUtc, documents[^1].Id)
            : null;
        return new DocumentPage(documents, nextCursor);
    }

    public Task<DocumentRecord?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return GetAccessibleAsync(id, ResourceAccessContext.System, cancellationToken);
    }

    public Task<DocumentRecord?> GetAccessibleAsync(
        Guid id,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default)
    {
        return ApplyAccess(dbContext.Documents
            .AsNoTracking()
            .Where(document => document.Id == id
                && document.LifecycleState == ResourceLifecycleStates.Active),
            access,
            DocumentPermissions.Read)
            .Select(document => new DocumentRecord(
                document.Id,
                document.OriginalFileName,
                document.MediaType,
                document.Extension,
                document.SizeBytes,
                document.Sha256,
                document.CreatedAtUtc,
                document.ParseRuns
                    .Where(run => run.LifecycleState == ResourceLifecycleStates.Active)
                    .OrderByDescending(run => run.CreatedAtUtc)
                    .Select(run => run.Status)
                    .FirstOrDefault(),
                access.IsInteractiveUser
                    && document.OwnerIssuer == access.Issuer
                    && document.OwnerSubject == access.Subject))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<DocumentContent?> OpenContentAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await OpenAccessibleContentAsync(id, ResourceAccessContext.System, cancellationToken);
    }

    public async Task<DocumentContent?> OpenAccessibleContentAsync(
        Guid id,
        ResourceAccessContext access,
        CancellationToken cancellationToken = default)
    {
        var storedDocument = await ApplyAccess(dbContext.Documents
            .AsNoTracking()
            .Where(document => document.Id == id
                && document.LifecycleState == ResourceLifecycleStates.Active),
            access,
            DocumentPermissions.Read)
            .Select(document => new StoredDocument(
                new DocumentRecord(
                    document.Id,
                    document.OriginalFileName,
                    document.MediaType,
                    document.Extension,
                    document.SizeBytes,
                    document.Sha256,
                    document.CreatedAtUtc,
                    document.ParseRuns
                        .Where(run => run.LifecycleState == ResourceLifecycleStates.Active)
                        .OrderByDescending(run => run.CreatedAtUtc)
                        .Select(run => run.Status)
                        .FirstOrDefault(),
                    access.IsInteractiveUser
                        && document.OwnerIssuer == access.Issuer
                        && document.OwnerSubject == access.Subject),
                document.StorageRef))
            .SingleOrDefaultAsync(cancellationToken);

        if (storedDocument is null)
        {
            return null;
        }

        try
        {
            var content = await fileStorage.OpenReadAsync(
                storedDocument.StorageRef,
                cancellationToken);
            return new DocumentContent(storedDocument.Document, content);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogError(
                exception,
                "Stored content for Document {DocumentId} is missing.",
                id);
            throw new DocumentContentUnavailableException(id, exception);
        }
    }

    private static IQueryable<DocumentEntity> ApplyAccess(
        IQueryable<DocumentEntity> query,
        ResourceAccessContext access,
        DocumentPermissions permission)
    {
        if (access.IsAdministrator || access.IsServiceClient)
        {
            return query;
        }

        if (!access.IsInteractiveUser)
        {
            return query.Where(_ => false);
        }

        var required = (int)permission;
        return query.Where(document =>
            (document.OwnerIssuer == access.Issuer && document.OwnerSubject == access.Subject)
            || document.AccessGrants.Any(grant =>
                grant.PrincipalIssuer == access.Issuer
                && grant.PrincipalSubject == access.Subject
                && (grant.Permissions & required) == required));
    }

    private static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Document cursor timestamp must use UTC.", nameof(value));
        }
    }

    private sealed record StoredDocument(
        DocumentRecord Document,
        string StorageRef);
}
