namespace StructaDoc.Contracts.Authentication;

/// <summary>
/// <paramref name="IsCurrent"/> lets a client hide the actions an administrator may not perform on
/// their own account without having to compare identifiers it would otherwise not need.
/// </summary>
public sealed record AdministratorAccountResponse(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc,
    bool IsCurrent);

public sealed record CreateAdministratorRequest(
    string Username,
    string Password,
    string? DisplayName);

public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetAdministratorPasswordRequest(string NewPassword);

public sealed record SetAdministratorActiveRequest(bool IsActive);
