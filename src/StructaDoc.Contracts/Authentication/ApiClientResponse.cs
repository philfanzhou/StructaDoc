namespace StructaDoc.Contracts.Authentication;

public sealed record ApiClientResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> Scopes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? RevokedAt);

public sealed record ApiClientCredentialResponse(
    ApiClientResponse Client,
    string Credential);
