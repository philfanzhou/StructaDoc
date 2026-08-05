namespace StructaDoc.Host.Authentication;

public static class AuthorizationPolicies
{
    public const string AdministratorLoginRateLimit = "administrator-login";

    public const string Administrator = "administrator";
    public const string DocumentsWrite = "documents:write";
}
