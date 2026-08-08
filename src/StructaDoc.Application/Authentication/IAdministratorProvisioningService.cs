namespace StructaDoc.Application.Authentication;

public enum AdministratorClaimOutcome
{
    Created,
    AlreadyClaimed,
    UsernameInUse,
    InvalidPassword,
    InvalidUsername,
}

public sealed record AdministratorClaimResult(
    AdministratorClaimOutcome Outcome,
    AuthenticatedAdministrator? Administrator);

public sealed record SetupClaimRecord(
    Guid AdministratorId,
    string ClaimedFromAddress,
    DateTime ClaimedAtUtc,
    DateTime? AcknowledgedAtUtc);

public interface IAdministratorProvisioningService
{
    /// <summary>
    /// Whether any administrator exists, active or disabled. First-run setup stays open only while
    /// this is false, so a disabled account still closes it.
    /// </summary>
    Task<bool> AnyAdministratorExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the first administrator. The service cannot authenticate the caller during first run,
    /// so the claim is atomic against concurrent attempts and records where it came from.
    /// </summary>
    Task<AdministratorClaimResult> ClaimFirstAdministratorAsync(
        string username,
        string password,
        string? displayName,
        string claimedFromAddress,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The recorded claim while an administrator has not yet confirmed that it was them. Drives the
    /// warning shown to every signed-in user, which is the compensating control for an open claim.
    /// </summary>
    Task<SetupClaimRecord?> GetUnacknowledgedClaimAsync(
        CancellationToken cancellationToken = default);

    Task AcknowledgeClaimAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
