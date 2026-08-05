namespace StructaDoc.Application.Authentication;

public sealed record ApiClientDefinition
{
    public const int MaximumNameLength = 255;

    private ApiClientDefinition(string name, IReadOnlyList<string> scopes)
    {
        Name = name;
        Scopes = scopes;
    }

    public string Name { get; }

    public IReadOnlyList<string> Scopes { get; }

    public static bool TryCreate(
        string? name,
        IEnumerable<string?>? scopes,
        out ApiClientDefinition? definition,
        out string errorField,
        out string errorMessage)
    {
        definition = null;
        var normalizedName = name?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            errorField = "name";
            errorMessage = "API Client name is required.";
            return false;
        }

        if (normalizedName.Length > MaximumNameLength)
        {
            errorField = "name";
            errorMessage = $"API Client name cannot exceed {MaximumNameLength} characters.";
            return false;
        }

        if (scopes is null)
        {
            errorField = "scopes";
            errorMessage = "API Client scopes are required; use an empty array for no permissions.";
            return false;
        }

        var normalizedScopes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var scopeValue in scopes)
        {
            var scope = scopeValue?.Trim();

            if (string.IsNullOrEmpty(scope) || !AuthenticationScopes.IsKnown(scope))
            {
                errorField = "scopes";
                errorMessage = $"Unknown API Client scope '{scopeValue}'.";
                return false;
            }

            normalizedScopes.Add(scope);
        }

        definition = new ApiClientDefinition(normalizedName, normalizedScopes.ToArray());
        errorField = string.Empty;
        errorMessage = string.Empty;
        return true;
    }
}
