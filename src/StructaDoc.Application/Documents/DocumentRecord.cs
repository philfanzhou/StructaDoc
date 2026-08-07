namespace StructaDoc.Application.Documents;

public sealed record DocumentRecord(
    Guid Id,
    string OriginalFileName,
    string MediaType,
    string Extension,
    long SizeBytes,
    string Sha256,
    DateTime CreatedAtUtc,
    string? LatestParseStatus = null,
    bool OwnedByCurrentUser = false);

public sealed record DocumentCursor(
    DateTime CreatedAtUtc,
    Guid Id);

public sealed record DocumentPage(
    IReadOnlyList<DocumentRecord> Items,
    DocumentCursor? NextCursor);

public sealed record DocumentContent(
    DocumentRecord Document,
    Stream Content);
