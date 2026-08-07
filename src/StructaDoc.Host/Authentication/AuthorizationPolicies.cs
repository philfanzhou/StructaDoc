namespace StructaDoc.Host.Authentication;

public static class AuthorizationPolicies
{
    public const string AdministratorLoginRateLimit = "administrator-login";

    public const string Administrator = "administrator";
    public const string DocumentsRead = "documents:read";
    public const string DocumentsWrite = "documents:write";
    public const string ParsesRead = "parses:read";
    public const string ParsesWrite = "parses:write";
    public const string InteractiveUser = "interactive-user";
}
