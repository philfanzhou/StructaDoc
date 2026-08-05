namespace StructaDoc.Contracts.Documents;

public sealed record DocumentResponse(
    Guid Id,
    string OriginalFileName,
    string MediaType,
    string Extension,
    long SizeBytes,
    string Sha256,
    DateTime CreatedAt);

public sealed record DocumentListResponse(
    IReadOnlyList<DocumentResponse> Items,
    string? NextCursor);
