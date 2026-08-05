namespace StructaDoc.Application.Documents;

public sealed class DocumentContentUnavailableException(Guid documentId, Exception innerException)
    : Exception($"Content for Document '{documentId:D}' is unavailable.", innerException)
{
    public Guid DocumentId { get; } = documentId;
}
