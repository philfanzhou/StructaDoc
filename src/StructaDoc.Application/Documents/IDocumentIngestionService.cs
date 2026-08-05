namespace StructaDoc.Application.Documents;

public interface IDocumentIngestionService
{
    Task<IngestedDocument> IngestAsync(
        DocumentIngestionRequest request,
        CancellationToken cancellationToken = default);
}
