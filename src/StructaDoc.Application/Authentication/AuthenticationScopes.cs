namespace StructaDoc.Application.Authentication;

public static class AuthenticationScopes
{
    public const string DocumentsRead = "documents:read";
    public const string DocumentsWrite = "documents:write";
    public const string ParsesRead = "parses:read";
    public const string ParsesWrite = "parses:write";

    /// <summary>
    /// The non-archiving layout-detection endpoint (ADR-0013). Unlike the four scopes above, this
    /// one is an API-client permission only: the endpoint it opens has no interactive-session use,
    /// so the Host's policy for it deliberately does not admit a signed-in browser session.
    /// </summary>
    public const string LayoutDetect = "layout:detect";

    /// <summary>
    /// The non-archiving image text-recognition endpoint (ADR-0015). An API-client permission only,
    /// like <see cref="LayoutDetect"/>, and independent of it: neither scope grants the other.
    /// </summary>
    public const string ImageRecognize = "image:recognize";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        [DocumentsRead, DocumentsWrite, ParsesRead, ParsesWrite, LayoutDetect, ImageRecognize]);

    public static bool IsKnown(string scope)
    {
        return scope is DocumentsRead or DocumentsWrite or ParsesRead or ParsesWrite or LayoutDetect
            or ImageRecognize;
    }
}
