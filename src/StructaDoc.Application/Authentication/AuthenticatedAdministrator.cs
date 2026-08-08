namespace StructaDoc.Application.Authentication;

public sealed record AuthenticatedAdministrator(
    Guid Id,
    string Username,
    string DisplayName,
    Guid SecurityStamp);
