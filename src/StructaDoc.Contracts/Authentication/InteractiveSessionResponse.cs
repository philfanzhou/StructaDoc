namespace StructaDoc.Contracts.Authentication;

public sealed record InteractiveSessionResponse(
    bool Authenticated,
    string? SubjectType,
    string? Subject,
    string? Issuer,
    string? DisplayName,
    string? Email,
    bool IsAdministrator,
    bool OidcEnabled);
