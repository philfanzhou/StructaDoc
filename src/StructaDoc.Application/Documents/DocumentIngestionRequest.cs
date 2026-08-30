using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.Documents;

public sealed record DocumentIngestionRequest(
    string OriginalFileName,
    string? DeclaredMediaType,
    Stream Content,
    CanonicalActor? CreatedBy = null,
    CanonicalActor? Owner = null);
