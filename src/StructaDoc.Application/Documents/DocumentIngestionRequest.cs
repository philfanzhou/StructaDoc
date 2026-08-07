namespace StructaDoc.Application.Documents;

public sealed record DocumentIngestionRequest(
    string OriginalFileName,
    string? DeclaredMediaType,
    Stream Content,
    string? CreatedBy = null,
    string? OwnerIssuer = null,
    string? OwnerSubject = null);
