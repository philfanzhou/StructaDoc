using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Authentication;
using StructaDoc.Infrastructure.Persistence;

namespace StructaDoc.Infrastructure.Authentication;

public sealed class AdministratorAuthenticationService(
    StructaDocDbContext dbContext,
    IPasswordHasher<Persistence.Entities.AdminUserEntity> passwordHasher,
    AdministratorPasswordVerifier passwordVerifier) : IAdministratorAuthenticationService
{
    internal const int MaximumPasswordLength = 1024;

    public async Task<AuthenticatedAdministrator?> AuthenticateAsync(
        string email,
        string password,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc);
        var suppliedPassword = password is not null && password.Length <= MaximumPasswordLength
            ? password
            : string.Empty;
        var normalizedEmail = NormalizeEmail(email);
        var user = normalizedEmail is null
            ? null
            : await dbContext.AdminUsers.SingleOrDefaultAsync(
                candidate => candidate.NormalizedEmail == normalizedEmail,
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
            user.Email,
            user.DisplayName,
            user.SecurityStamp);
    }

    internal static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320)
        {
            return null;
        }

        return email.Trim().ToUpperInvariant();
    }

    private static void ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Authentication timestamps must use UTC.", nameof(value));
        }
    }
}
