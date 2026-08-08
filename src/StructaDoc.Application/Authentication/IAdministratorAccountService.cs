namespace StructaDoc.Application.Authentication;

public sealed record AdministratorAccountRecord(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);

public enum AdministratorAccountStatus
{
    Succeeded,
    NotFound,
    InvalidUsername,
    InvalidPassword,
    UsernameInUse,
    IncorrectPassword,

    /// <summary>
    /// The account is the only active administrator left. Disabling or deleting it would leave the
    /// deployment with no way to administer itself except reconfiguring a bootstrap administrator.
    /// </summary>
    LastActiveAdministrator,
}

public sealed record AdministratorAccountResult(
    AdministratorAccountStatus Status,
    AdministratorAccountRecord? Account = null);

/// <summary>
/// Carries the administrator back after a password change so the caller's own session can be
/// re-issued: changing a password rotates the security stamp, which is exactly what invalidates
/// every cookie already holding the old one, including the caller's.
/// </summary>
public sealed record AdministratorPasswordChangeResult(
    AdministratorAccountStatus Status,
    AuthenticatedAdministrator? Administrator = null);

public interface IAdministratorAccountService
{
    Task<IReadOnlyList<AdministratorAccountRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<AdministratorAccountResult> CreateAsync(
        string username,
        string password,
        string? displayName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<AdministratorPasswordChangeResult> ChangeOwnPasswordAsync(
        Guid administratorId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);

    Task<AdministratorAccountStatus> ResetPasswordAsync(
        Guid administratorId,
        string newPassword,
        CancellationToken cancellationToken = default);

    Task<AdministratorAccountStatus> SetActiveAsync(
        Guid administratorId,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<AdministratorAccountStatus> DeleteAsync(
        Guid administratorId,
        CancellationToken cancellationToken = default);
}
