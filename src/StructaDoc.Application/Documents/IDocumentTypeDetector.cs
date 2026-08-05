namespace StructaDoc.Application.Documents;

public interface IDocumentTypeDetector
{
    Task<DetectedDocumentType?> DetectAsync(
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken = default);
}
