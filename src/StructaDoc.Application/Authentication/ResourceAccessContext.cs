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

// Each value is a bit in a stored grant, so the numbers are part of the database contract and a
// permission cannot be renumbered. Bit 2 is retired: it was `Write`, which nothing ever checked
// because no operation modifies a Document in place. Grants written before it was withdrawn still
// carry that bit, and reading one is harmless — no route asks for it and the grant endpoint no
// longer renders a name for it. Assigning 2 to a new permission would silently hand those grants
// whatever it comes to mean, so a permission added here takes the next unused bit, 64.
[Flags]
public enum DocumentPermissions
{
    None = 0,
    Read = 1,
    Parse = 4,
    Export = 8,
    Delete = 16,
    Share = 32,
    All = Read | Parse | Export | Delete | Share,
}
