namespace StructaDoc.Contracts.Authentication;

public sealed record AdministratorLoginRequest(
    string Email,
    string Password);
