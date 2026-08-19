namespace StructaDoc.Application.Authentication;

// A principal that can own a Document or be named by an access grant. There are two kinds and they
// share one key space, `(issuer, subject)`: an OIDC user, whose issuer is the identity provider's,
// and an API client, whose issuer is the reserved value below.
//
// The reserved issuer cannot collide with a real one. `ExternalIdentityConstraints.IsValidIssuer`
// requires an absolute `http`/`https` URI, and `structadoc:` is neither, so no identity provider can
// present this issuer and no OIDC subject can be mistaken for a client ID.
//
// Sharing the key space is what lets one owner-or-grant filter serve both kinds. An API client is
// not a global service principal that sees the whole deployment; it sees what it owns and what it
// was granted, on the same terms as a person.
public static class PrincipalIdentity
{
    public const string ApiClientIssuer = "structadoc:api-client";

    public static string ApiClientSubject(Guid clientId) => clientId.ToString("D");

    public static bool IsApiClient(string? issuer) =>
        string.Equals(issuer, ApiClientIssuer, StringComparison.Ordinal);

    // An API client subject is the client ID, so it is validated as one rather than against the
    // OIDC subject rules. A grant naming a client that does not exist is still accepted, for the
    // same reason a grant may name an OIDC subject that has never signed in: the identity is
    // authoritative elsewhere, and refusing it here would only mean it could not be prepared ahead
    // of first use.
    public static bool IsValid(string? issuer, string? subject) => IsApiClient(issuer)
        ? Guid.TryParseExact(subject, "D", out _)
        : ExternalIdentityConstraints.IsValidIssuer(issuer)
            && ExternalIdentityConstraints.IsValidSubject(subject);
}
