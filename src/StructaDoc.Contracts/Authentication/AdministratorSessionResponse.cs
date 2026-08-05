namespace StructaDoc.Contracts.Authentication;

public sealed record AdministratorSessionResponse(
    Guid Id,
    string Email,
    string DisplayName);
