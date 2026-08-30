using System.Security.Claims;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Host.Authentication;

public static class ResourceAccessContextFactory
{
    public static ResourceAccessContext Create(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subjectType = principal.FindFirstValue(StructaDocClaimTypes.SubjectType);
        var isAdministrator = string.Equals(subjectType, SubjectTypes.Administrator, StringComparison.Ordinal)
            || principal.HasClaim(StructaDocClaimTypes.Administrator, bool.TrueString);
        if (isAdministrator)
        {
            return new ResourceAccessContext(IsAdministrator: true, Issuer: null, Subject: null);
        }

        // An API client is a principal in the same key space as an OIDC user, so it is bounded by
        // ownership and grants rather than reaching everything its scopes allow a verb on. Its
        // subject is the client ID, which is what the key already identifies it by.
        if (string.Equals(subjectType, SubjectTypes.ApiClient, StringComparison.Ordinal))
        {
            return new ResourceAccessContext(
                IsAdministrator: false,
                PrincipalIdentity.ApiClientIssuer,
                principal.FindFirstValue(ClaimTypes.NameIdentifier));
        }

        return new ResourceAccessContext(
            IsAdministrator: false,
            principal.FindFirstValue(StructaDocClaimTypes.ExternalIssuer),
            principal.FindFirstValue(StructaDocClaimTypes.ExternalSubject));
    }
}
