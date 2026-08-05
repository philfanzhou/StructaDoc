namespace StructaDoc.Application.Authentication;

public interface IAdministratorAuthenticationService
{
    Task<AuthenticatedAdministrator?> AuthenticateAsync(
        string email,
        string password,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
