namespace StructaDoc.Contracts.Setup;

public sealed record SetupStatusResponse(bool SetupRequired);

public sealed record SetupClaimRequest(
    string Username,
    string Password,
    string? DisplayName);

/// <summary>
/// Reported to administrators until one confirms the claim. First-run setup cannot authenticate the
/// caller, so an unexpected claimant address is the signal that someone else reached the service
/// first.
/// </summary>
public sealed record SetupClaimWarningResponse(
    string ClaimedFromAddress,
    DateTime ClaimedAtUtc);
