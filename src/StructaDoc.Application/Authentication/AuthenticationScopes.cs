namespace StructaDoc.Application.Authentication;

public static class AuthenticationScopes
{
    public const string DocumentsRead = "documents:read";
    public const string DocumentsWrite = "documents:write";
    public const string ParsesRead = "parses:read";
    public const string ParsesWrite = "parses:write";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        [DocumentsRead, DocumentsWrite, ParsesRead, ParsesWrite]);

    public static bool IsKnown(string scope)
    {
        return scope is DocumentsRead or DocumentsWrite or ParsesRead or ParsesWrite;
    }
}
