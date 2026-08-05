using Microsoft.Extensions.Logging;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Storage;
using StructaDoc.Infrastructure.Persistence;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Documents;

public sealed class EfCoreDocumentIngestionService(
    StructaDocDbContext dbContext,
    IFileStorage fileStorage,
    IDocumentTypeDetector documentTypeDetector,
    DocumentIngestionOptions options,
    TimeProvider timeProvider,
    ILogger<EfCoreDocumentIngestionService> logger) : IDocumentIngestionService
{
    public async Task<IngestedDocument> IngestAsync(
        DocumentIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);
        options.Validate();

        var originalFileName = NormalizeFileName(request.OriginalFileName);
        var documentId = Guid.NewGuid();
        var storageRef = $"documents/{documentId:N}/original";
        StoredFile? storedFile = null;

        try
        {
            storedFile = await fileStorage.WriteAsync(
                storageRef,
                request.Content,
                options.MaxUploadBytes,
                cancellationToken);

            await using var storedContent = await fileStorage.OpenReadAsync(
                storageRef,
                cancellationToken);
            var detectedType = await documentTypeDetector.DetectAsync(
                storedContent,
                originalFileName,
                cancellationToken) ?? throw new UnsupportedDocumentTypeException();
            var createdAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var entity = new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = originalFileName,
                MediaType = detectedType.MediaType,
                Extension = detectedType.Extension,
                SizeBytes = storedFile.SizeBytes,
                Sha256 = storedFile.Sha256,
                StorageRef = storedFile.StorageRef,
                CreatedBy = NormalizeCreatedBy(request.CreatedBy),
                CreatedAtUtc = createdAtUtc,
            };

            dbContext.Documents.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new IngestedDocument(
                entity.Id,
                entity.OriginalFileName,
                entity.MediaType,
                entity.Extension,
                entity.SizeBytes,
                entity.Sha256,
                entity.CreatedAtUtc);
        }
        catch
        {
            if (storedFile is not null)
            {
                await DeleteAfterFailureAsync(storedFile.StorageRef);
            }

            throw;
        }
    }

    private async Task DeleteAfterFailureAsync(string storageRef)
    {
        try
        {
            await fileStorage.DeleteIfExistsAsync(storageRef, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to delete stored object {StorageRef} after Document ingestion failed.",
                storageRef);
        }
    }

    private static string NormalizeFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var normalizedPath = fileName.Replace('\\', '/');
        var normalizedFileName = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..].Trim();

        if (normalizedFileName.Length is 0 or > 255)
        {
            throw new ArgumentException(
                "Original file name must contain between 1 and 255 characters.",
                nameof(fileName));
        }

        if (normalizedFileName.Any(char.IsControl))
        {
            throw new ArgumentException("Original file name contains control characters.", nameof(fileName));
        }

        return normalizedFileName;
    }

    private static string? NormalizeCreatedBy(string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
        {
            return null;
        }

        var normalized = createdBy.Trim();
        return normalized.Length <= 255
            ? normalized
            : throw new ArgumentException("Creator ID cannot exceed 255 characters.", nameof(createdBy));
    }
}
