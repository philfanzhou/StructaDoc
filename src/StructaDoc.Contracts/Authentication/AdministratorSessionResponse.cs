namespace StructaDoc.Contracts.Authentication;

public sealed record AdministratorSessionResponse(
    Guid Id,
    string Username,
    string DisplayName);
