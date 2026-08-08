namespace StructaDoc.Contracts.Authentication;

public sealed record AdministratorLoginRequest(
    string Username,
    string Password);
