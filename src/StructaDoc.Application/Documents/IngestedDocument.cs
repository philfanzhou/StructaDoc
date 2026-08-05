namespace StructaDoc.Application.Documents;

public sealed record IngestedDocument(
    Guid Id,
    string OriginalFileName,
    string MediaType,
    string Extension,
    long SizeBytes,
    string Sha256,
    DateTime CreatedAtUtc);
