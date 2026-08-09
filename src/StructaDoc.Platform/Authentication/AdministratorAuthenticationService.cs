using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Authentication;
using StructaDoc.Platform.ControlPlane;

namespace StructaDoc.Platform.Authentication;

public sealed class AdministratorAuthenticationService(
    ControlPlaneDbContext dbContext,
    IPasswordHasher<ControlPlane.Entities.AdminUserEntity> passwordHasher,
    AdministratorPasswordVerifier passwordVerifier) : IAdministratorAuthenticationService
{
    public async Task<AuthenticatedAdministrator?> AuthenticateAsync(
        string username,
        string password,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc);
        var suppliedPassword =
            password is not null && password.Length <= AdministratorPasswordPolicy.MaximumLength
                ? password
                : string.Empty;
        var normalizedUsername = AdministratorUsernamePolicy.Normalize(username);
        var user = normalizedUsername is null
            ? null
            : await dbContext.AdminUsers.SingleOrDefaultAsync(
                candidate => candidate.NormalizedUsername == normalizedUsername,
                cancellationToken);
        var verificationResult = passwordVerifier.Verify(user, suppliedPassword);

        if (user is null
            || !user.IsActive
            || verificationResult == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, suppliedPassword);
        }

        user.LastLoginAtUtc = nowUtc;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthenticatedAdministrator(
            user.Id,
            user.Username,
            user.DisplayName,
            user.SecurityStamp);
    }

    private static void ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Authentication timestamps must use UTC.", nameof(value));
        }
    }
}
