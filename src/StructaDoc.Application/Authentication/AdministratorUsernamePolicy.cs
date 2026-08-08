namespace StructaDoc.Application.Authentication;

/// <summary>
/// The local administrator is identified by a username rather than an email address. The account is
/// local to one deployment and never federated, so an email address would imply a mailbox the
/// service neither verifies nor uses.
/// </summary>
public static class AdministratorUsernamePolicy
{
    public const int MinimumLength = 3;

    public const int MaximumLength = 64;

    public static bool IsAcceptable(string? username)
    {
        if (username is null)
        {
            return false;
        }

        var trimmed = username.Trim();
        if (trimmed.Length < MinimumLength || trimmed.Length > MaximumLength)
        {
            return false;
        }

        if (!IsAlphanumeric(trimmed[0]) || !IsAlphanumeric(trimmed[^1]))
        {
            return false;
        }

        return trimmed.All(character =>
            IsAlphanumeric(character)
            || character is '.' or '_' or '-');
    }

    /// <summary>
    /// Uniqueness is case-insensitive so two accounts cannot differ only by letter case.
    /// </summary>
    public static string? Normalize(string? username)
    {
        return IsAcceptable(username) ? username!.Trim().ToUpperInvariant() : null;
    }

    private static bool IsAlphanumeric(char character)
    {
        return character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
    }
}
