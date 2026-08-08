namespace StructaDoc.Application.Authentication;

public static class AdministratorPasswordPolicy
{
    public const int MinimumLength = 12;

    public const int MaximumLength = 1024;

    public static bool IsAcceptable(string? password)
    {
        return password is not null
            && password.Length >= MinimumLength
            && password.Length <= MaximumLength;
    }
}
