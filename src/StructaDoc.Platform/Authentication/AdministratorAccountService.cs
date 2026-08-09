using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Authentication;
using StructaDoc.Platform.ControlPlane;
using StructaDoc.Platform.ControlPlane.Entities;

namespace StructaDoc.Platform.Authentication;

public sealed class AdministratorAccountService(
    ControlPlaneDbContext dbContext,
    IPasswordHasher<AdminUserEntity> passwordHasher,
    AdministratorPasswordVerifier passwordVerifier) : IAdministratorAccountService
{
    public async Task<IReadOnlyList<AdministratorAccountRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return await dbContext.AdminUsers
            .AsNoTracking()
            .OrderBy(user => user.CreatedAtUtc)
            .ThenBy(user => user.Id)
            .Select(user => new AdministratorAccountRecord(
                user.Id,
                user.Username,
                user.DisplayName,
                user.IsActive,
                user.CreatedAtUtc,
                user.LastLoginAtUtc))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<AdministratorAccountResult> CreateAsync(
        string username,
        string password,
        string? displayName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc);

        var normalizedUsername = AdministratorUsernamePolicy.Normalize(username);
        if (normalizedUsername is null)
        {
            return new AdministratorAccountResult(AdministratorAccountStatus.InvalidUsername);
        }

        if (!AdministratorPasswordPolicy.IsAcceptable(password))
        {
            return new AdministratorAccountResult(AdministratorAccountStatus.InvalidPassword);
        }

        var trimmedUsername = username.Trim();
        var administrator = new AdminUserEntity
        {
            Id = Guid.NewGuid(),
            Username = trimmedUsername,
            NormalizedUsername = normalizedUsername,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? trimmedUsername
                : displayName.Trim(),
            PasswordHash = string.Empty,
            IsActive = true,
            SecurityStamp = Guid.NewGuid(),
            CreatedAtUtc = nowUtc,
        };
        administrator.PasswordHash = passwordHasher.HashPassword(administrator, password);
        dbContext.AdminUsers.Add(administrator);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index on the normalized username is the arbiter, not a preceding read, so
            // two administrators creating the same name concurrently cannot both succeed.
            dbContext.ChangeTracker.Clear();
            return new AdministratorAccountResult(AdministratorAccountStatus.UsernameInUse);
        }

        return new AdministratorAccountResult(
            AdministratorAccountStatus.Succeeded,
            ToRecord(administrator));
    }

    public async Task<AdministratorPasswordChangeResult> ChangeOwnPasswordAsync(
        Guid administratorId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var administrator = await dbContext.AdminUsers.SingleOrDefaultAsync(
            candidate => candidate.Id == administratorId,
            cancellationToken);
        if (administrator is null)
        {
            return new AdministratorPasswordChangeResult(AdministratorAccountStatus.NotFound);
        }

        if (!AdministratorPasswordPolicy.IsAcceptable(newPassword))
        {
            return new AdministratorPasswordChangeResult(
                AdministratorAccountStatus.InvalidPassword);
        }

        var supplied = currentPassword is not null
            && currentPassword.Length <= AdministratorPasswordPolicy.MaximumLength
                ? currentPassword
                : string.Empty;
        if (passwordVerifier.Verify(administrator, supplied) == PasswordVerificationResult.Failed)
        {
            return new AdministratorPasswordChangeResult(
                AdministratorAccountStatus.IncorrectPassword);
        }

        await SetPasswordAsync(administrator, newPassword, cancellationToken);

        return new AdministratorPasswordChangeResult(
            AdministratorAccountStatus.Succeeded,
            new AuthenticatedAdministrator(
                administrator.Id,
                administrator.Username,
                administrator.DisplayName,
                administrator.SecurityStamp));
    }

    public async Task<AdministratorAccountStatus> ResetPasswordAsync(
        Guid administratorId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!AdministratorPasswordPolicy.IsAcceptable(newPassword))
        {
            return AdministratorAccountStatus.InvalidPassword;
        }

        var administrator = await dbContext.AdminUsers.SingleOrDefaultAsync(
            candidate => candidate.Id == administratorId,
            cancellationToken);
        if (administrator is null)
        {
            return AdministratorAccountStatus.NotFound;
        }

        await SetPasswordAsync(administrator, newPassword, cancellationToken);
        return AdministratorAccountStatus.Succeeded;
    }

    public async Task<AdministratorAccountStatus> SetActiveAsync(
        Guid administratorId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        if (isActive)
        {
            var enabled = await dbContext.AdminUsers
                .Where(user => user.Id == administratorId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(user => user.IsActive, true),
                    cancellationToken);
            return enabled == 0
                ? AdministratorAccountStatus.NotFound
                : AdministratorAccountStatus.Succeeded;
        }

        var disabled = await SpareAdministrator(administratorId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(user => user.IsActive, false),
                cancellationToken);

        // A disabled administrator loses every session on its next request: the cookie carries a
        // security stamp, and validation rejects it once the account is inactive.
        return disabled > 0
            ? AdministratorAccountStatus.Succeeded
            : await ExplainRejectionAsync(administratorId, cancellationToken);
    }

    public async Task<AdministratorAccountStatus> DeleteAsync(
        Guid administratorId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await SpareAdministrator(administratorId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0
            ? AdministratorAccountStatus.Succeeded
            : await ExplainRejectionAsync(administratorId, cancellationToken);
    }

    /// <summary>
    /// Narrows to the account only while it is not the last active administrator. The condition
    /// travels into the statement rather than being read first, so two requests removing the last
    /// two active administrators cannot both observe a spare and both succeed.
    /// </summary>
    private IQueryable<AdminUserEntity> SpareAdministrator(Guid administratorId)
    {
        var administrators = dbContext.AdminUsers;
        return administrators.Where(user =>
            user.Id == administratorId
            && (!user.IsActive || administrators.Count(other => other.IsActive) > 1));
    }

    private async Task<AdministratorAccountStatus> ExplainRejectionAsync(
        Guid administratorId,
        CancellationToken cancellationToken)
    {
        return await dbContext.AdminUsers.AnyAsync(
            candidate => candidate.Id == administratorId,
            cancellationToken)
            ? AdministratorAccountStatus.LastActiveAdministrator
            : AdministratorAccountStatus.NotFound;
    }

    private async Task SetPasswordAsync(
        AdminUserEntity administrator,
        string newPassword,
        CancellationToken cancellationToken)
    {
        administrator.PasswordHash = passwordHasher.HashPassword(administrator, newPassword);

        // Rotating the stamp is what makes a password change end sessions. Without it the old
        // password's cookies would stay valid for the rest of their lifetime.
        administrator.SecurityStamp = Guid.NewGuid();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AdministratorAccountRecord ToRecord(AdminUserEntity administrator)
    {
        return new AdministratorAccountRecord(
            administrator.Id,
            administrator.Username,
            administrator.DisplayName,
            administrator.IsActive,
            administrator.CreatedAtUtc,
            administrator.LastLoginAtUtc);
    }

    private static void ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Account timestamps must use UTC.", nameof(value));
        }
    }
}
