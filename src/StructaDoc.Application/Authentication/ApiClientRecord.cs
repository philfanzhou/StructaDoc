namespace StructaDoc.Application.Authentication;

public sealed record ApiClientRecord(
    Guid Id,
    string Name,
    IReadOnlyList<string> Scopes,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? RevokedAtUtc);

public sealed record IssuedApiClient(
    ApiClientRecord Client,
    string Credential);
