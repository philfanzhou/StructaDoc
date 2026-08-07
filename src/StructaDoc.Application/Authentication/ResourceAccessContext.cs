namespace StructaDoc.Application.Authentication;

public sealed record ResourceAccessContext(
    bool IsAdministrator,
    bool IsServiceClient,
    string? Issuer,
    string? Subject)
{
    public static ResourceAccessContext System { get; } = new(
        IsAdministrator: true,
        IsServiceClient: true,
        Issuer: null,
        Subject: null);

    public bool IsInteractiveUser =>
        !IsAdministrator
        && !IsServiceClient
        && !string.IsNullOrWhiteSpace(Issuer)
        && !string.IsNullOrWhiteSpace(Subject);
}

[Flags]
public enum DocumentPermissions
{
    None = 0,
    Read = 1,
    Write = 2,
    Parse = 4,
    Export = 8,
    Delete = 16,
    Share = 32,
    All = Read | Write | Parse | Export | Delete | Share,
}
