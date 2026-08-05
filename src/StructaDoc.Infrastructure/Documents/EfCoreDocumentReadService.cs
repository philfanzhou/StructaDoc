using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Storage;
using StructaDoc.Infrastructure.Persistence;

namespace StructaDoc.Infrastructure.Documents;

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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var query = dbContext.Documents.AsNoTracking();

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
                document.CreatedAtUtc))
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
        return dbContext.Documents
            .AsNoTracking()
            .Where(document => document.Id == id)
            .Select(document => new DocumentRecord(
                document.Id,
                document.OriginalFileName,
                document.MediaType,
                document.Extension,
                document.SizeBytes,
                document.Sha256,
                document.CreatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<DocumentContent?> OpenContentAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var storedDocument = await dbContext.Documents
            .AsNoTracking()
            .Where(document => document.Id == id)
            .Select(document => new StoredDocument(
                new DocumentRecord(
                    document.Id,
                    document.OriginalFileName,
                    document.MediaType,
                    document.Extension,
                    document.SizeBytes,
                    document.Sha256,
                    document.CreatedAtUtc),
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
