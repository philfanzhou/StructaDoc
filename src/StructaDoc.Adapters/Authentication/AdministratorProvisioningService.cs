using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.ControlPlane.Entities;

namespace StructaDoc.Adapters.Authentication;

public sealed class AdministratorProvisioningService(
    ControlPlaneDbContext dbContext,
    IPasswordHasher<AdminUserEntity> passwordHasher) : IAdministratorProvisioningService
{
    public Task<bool> AnyAdministratorExistsAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.AdminUsers.AnyAsync(cancellationToken);
    }

    public async Task<AdministratorClaimResult> ClaimFirstAdministratorAsync(
        string username,
        string password,
        string? displayName,
        string claimedFromAddress,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Claim timestamps must use UTC.", nameof(nowUtc));
        }

        var normalizedUsername = AdministratorUsernamePolicy.Normalize(username);
        if (normalizedUsername is null)
        {
            return new AdministratorClaimResult(AdministratorClaimOutcome.InvalidUsername, null);
        }

        if (!AdministratorPasswordPolicy.IsAcceptable(password))
        {
            return new AdministratorClaimResult(AdministratorClaimOutcome.InvalidPassword, null);
        }

        // A cheap early exit for the common closed case; the database below is the real arbiter.
        if (await dbContext.AdminUsers.AnyAsync(cancellationToken))
        {
            return new AdministratorClaimResult(AdministratorClaimOutcome.AlreadyClaimed, null);
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

        // That check cannot be trusted on its own: two concurrent claims with different usernames
        // would both pass it, and the unique username index would not catch that. The claim row uses
        // a fixed primary key, so exactly one insert can win regardless of the names chosen.
        dbContext.AdminUsers.Add(administrator);
        dbContext.SetupClaims.Add(new SetupClaimEntity
        {
            Id = SetupClaim.SingletonId,
            AdministratorId = administrator.Id,
            ClaimedFromAddress = Truncate(claimedFromAddress, 45),
            ClaimedAtUtc = nowUtc,
            AcknowledgedAtUtc = null,
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return new AdministratorClaimResult(AdministratorClaimOutcome.AlreadyClaimed, null);
        }

        return new AdministratorClaimResult(
            AdministratorClaimOutcome.Created,
            new AuthenticatedAdministrator(
                administrator.Id,
                administrator.Username,
                administrator.DisplayName,
                administrator.SecurityStamp));
    }

    public async Task<SetupClaimRecord?> GetUnacknowledgedClaimAsync(
        CancellationToken cancellationToken = default)
    {
        var claim = await dbContext.SetupClaims
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.AcknowledgedAtUtc == null,
                cancellationToken);

        return claim is null
            ? null
            : new SetupClaimRecord(
                claim.AdministratorId,
                claim.ClaimedFromAddress,
                claim.ClaimedAtUtc,
                claim.AcknowledgedAtUtc);
    }

    public async Task AcknowledgeClaimAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Claim timestamps must use UTC.", nameof(nowUtc));
        }

        var claim = await dbContext.SetupClaims.SingleOrDefaultAsync(
            candidate => candidate.AcknowledgedAtUtc == null,
            cancellationToken);
        if (claim is null)
        {
            return;
        }

        claim.AcknowledgedAtUtc = nowUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
