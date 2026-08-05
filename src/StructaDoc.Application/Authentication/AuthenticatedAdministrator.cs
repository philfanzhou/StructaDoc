namespace StructaDoc.Application.Authentication;

public sealed record AuthenticatedAdministrator(
    Guid Id,
    string Email,
    string DisplayName,
    Guid SecurityStamp);
