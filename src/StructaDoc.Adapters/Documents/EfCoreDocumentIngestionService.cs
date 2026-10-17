using Microsoft.Extensions.Logging;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Storage;

namespace StructaDoc.Adapters.Documents;

public sealed class EfCoreDocumentIngestionService(
    StructaDocDbContext dbContext,
    IFileStorage fileStorage,
    IDocumentTypeDetector documentTypeDetector,
    DocumentUploadLimits uploadLimits,
    TimeProvider timeProvider,
    ILogger<EfCoreDocumentIngestionService> logger) : IDocumentIngestionService
{
    public async Task<IngestedDocument> IngestAsync(
        DocumentIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);

        var originalFileName = NormalizeFileName(request.OriginalFileName);
        var documentId = Guid.NewGuid();
        var storageRef = $"documents/{documentId:N}/original";
        StoredFile? storedFile = null;

        try
        {
            // The size limit is read per ingestion from the running holder rather than the startup
            // options snapshot, which is what lets an administrator change it without a restart.
            // This write is the authoritative rejection: nothing before it decides what "too
            // large" means for the document itself.
            storedFile = await fileStorage.WriteAsync(
                storageRef,
                request.Content,
                uploadLimits.MaxUploadBytes,
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
                CreatedByIssuer = request.CreatedBy?.EncodeIssuer(),
                CreatedBySubject = request.CreatedBy?.EncodeSubject(),
                OwnerIssuer = request.Owner?.EncodeIssuer(),
                OwnerSubject = request.Owner?.EncodeSubject(),
                CreatedAtUtc = createdAtUtc,
            };

            if (request.Owner is not null
                && !PrincipalIdentity.IsValid(request.Owner.Issuer, request.Owner.Subject))
            {
                throw new ArgumentException("Document owner is neither an OIDC issuer and subject pair nor an API client.");
            }

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

}
