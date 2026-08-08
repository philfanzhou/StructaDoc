namespace StructaDoc.Application.Authentication;

public static class AdministratorPasswordPolicy
{
    /// <summary>
    /// Sign-in is rate limited per address and the account exists on one deployment rather than on a
    /// public service, so the length is a floor against trivial passwords rather than the control
    /// that carries the account.
    /// </summary>
    public const int MinimumLength = 8;

    public const int MaximumLength = 1024;

    public static bool IsAcceptable(string? password)
    {
        return password is not null
            && password.Length >= MinimumLength
            && password.Length <= MaximumLength;
    }
}
