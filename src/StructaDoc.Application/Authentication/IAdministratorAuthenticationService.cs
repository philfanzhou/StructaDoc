namespace StructaDoc.Application.Authentication;

public interface IAdministratorAuthenticationService
{
    Task<AuthenticatedAdministrator?> AuthenticateAsync(
        string username,
        string password,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
