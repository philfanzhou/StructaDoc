using Microsoft.OpenApi;

namespace StructaDoc.Host.OpenApi;

// The groups the description is read in. Without them every operation lands under the assembly
// name, and a reader is handed one undifferentiated list with nothing saying where to start.
internal static class ApiTags
{
    public const string Documents = "Documents";
    public const string ParseRuns = "Parse Runs";
    public const string Administration = "Administration";
    public const string Sessions = "Sessions";
    public const string System = "System";

    public static HashSet<OpenApiTag> Describe() =>
    [
        new()
        {
            Name = Documents,
            Description = "Upload, list, download, delete, and share documents.",
        },
        new()
        {
            Name = ParseRuns,
            Description = "Start parsing, follow it, and read the normalized result.",
        },
        new()
        {
            Name = Administration,
            Description = "Browser-only. Providers, API clients, settings, and administrator accounts.",
        },
        new()
        {
            Name = Sessions,
            Description = "Browser-only. Sign-in, first-run setup, and the antiforgery token.",
        },
        new()
        {
            Name = System,
            Description = "What this deployment is.",
        },
    ];
}
