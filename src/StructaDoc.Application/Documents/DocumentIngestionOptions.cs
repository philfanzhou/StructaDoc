namespace StructaDoc.Application.Documents;

public sealed class DocumentIngestionOptions
{
    public const string SectionName = "Documents";

    public bool UploadApiEnabled { get; init; }

    public long MaxUploadBytes { get; init; } = 100 * 1024 * 1024;

    public void Validate()
    {
        if (MaxUploadBytes <= 0)
        {
            throw new InvalidOperationException("Documents:MaxUploadBytes must be positive.");
        }
    }
}
