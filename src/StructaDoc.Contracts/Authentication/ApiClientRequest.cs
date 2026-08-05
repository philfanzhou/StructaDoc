namespace StructaDoc.Contracts.Authentication;

public sealed record ApiClientRequest(
    string? Name,
    IReadOnlyList<string?>? Scopes);
