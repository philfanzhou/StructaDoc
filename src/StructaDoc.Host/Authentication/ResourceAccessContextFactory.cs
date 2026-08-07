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
        var isServiceClient = string.Equals(subjectType, SubjectTypes.ApiClient, StringComparison.Ordinal);
        return new ResourceAccessContext(
            isAdministrator,
            isServiceClient,
            principal.FindFirstValue(StructaDocClaimTypes.ExternalIssuer),
            principal.FindFirstValue(StructaDocClaimTypes.ExternalSubject));
    }

    public static string GetActorId(ClaimsPrincipal principal)
    {
        var subjectType = principal.FindFirstValue(StructaDocClaimTypes.SubjectType)
            ?? throw new InvalidOperationException("Authenticated subject type is missing.");
        if (string.Equals(subjectType, SubjectTypes.User, StringComparison.Ordinal))
        {
            var issuer = principal.FindFirstValue(StructaDocClaimTypes.ExternalIssuer)
                ?? throw new InvalidOperationException("OIDC issuer is missing.");
            var subject = principal.FindFirstValue(StructaDocClaimTypes.ExternalSubject)
                ?? throw new InvalidOperationException("OIDC subject is missing.");
            return $"oidc:{issuer}|{subject}";
        }

        return $"{subjectType}:{principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated subject ID is missing.")}";
    }
}
