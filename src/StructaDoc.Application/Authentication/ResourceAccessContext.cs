namespace StructaDoc.Application.Authentication;

// Who is asking, in the only terms resource authorization needs. An administrator reaches every
// resource in the deployment. Everyone else reaches what their `(issuer, subject)` owns or was
// granted, and that includes API clients: an application holding a key is a principal in the
// workspace, not a second administrator with narrower verbs. See `PrincipalIdentity`.
public sealed record ResourceAccessContext(
    bool IsAdministrator,
    string? Issuer,
    string? Subject)
{
    // Workers and other in-process callers act on resources nobody requested, so they carry no
    // principal and are not bounded by one.
    public static ResourceAccessContext System { get; } = new(
        IsAdministrator: true,
        Issuer: null,
        Subject: null);

    public bool HasPrincipalIdentity =>
        !string.IsNullOrWhiteSpace(Issuer)
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
