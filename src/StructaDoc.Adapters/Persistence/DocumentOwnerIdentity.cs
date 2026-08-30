using StructaDoc.Application.Authentication;

namespace StructaDoc.Adapters.Persistence;

internal readonly record struct DocumentOwnerIdentity(byte[] Issuer, byte[] Subject)
{
    public static bool CanCompareTextGrant(
        ResourceAccessContext access,
        string? providerName) =>
        providerName?.StartsWith("Npgsql.", StringComparison.Ordinal) is not true
        || (access.Issuer?.Contains('\0', StringComparison.Ordinal) is not true
            && access.Subject?.Contains('\0', StringComparison.Ordinal) is not true);

    public static DocumentOwnerIdentity From(ResourceAccessContext access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!access.HasPrincipalIdentity)
        {
            throw new ArgumentException(
                "A principal identity is required to encode a Document owner.",
                nameof(access));
        }

        var actor = CanonicalActor.Create(access.Issuer!, access.Subject!);
        if (!PrincipalIdentity.IsValid(actor.Issuer, actor.Subject))
        {
            throw new InvalidOperationException(
                "The resource access identity is not a valid Document owner.");
        }

        return new DocumentOwnerIdentity(actor.EncodeIssuer(), actor.EncodeSubject());
    }
}
