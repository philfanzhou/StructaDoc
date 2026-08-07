namespace StructaDoc.Application.Authentication;

public static class ExternalIdentityConstraints
{
    public const int MaximumIssuerLength = 512;
    public const int MaximumSubjectLength = 255;

    public static bool IsValidIssuer(string? issuer)
    {
        return !string.IsNullOrWhiteSpace(issuer)
            && string.Equals(issuer, issuer.Trim(), StringComparison.Ordinal)
            && issuer.Length <= MaximumIssuerLength
            && IsAscii(issuer)
            && Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    public static bool IsValidSubject(string? subject)
    {
        return !string.IsNullOrWhiteSpace(subject)
            && string.Equals(subject, subject.Trim(), StringComparison.Ordinal)
            && subject.Length <= MaximumSubjectLength
            && IsAscii(subject);
    }

    private static bool IsAscii(string value) => value.All(character => character <= 0x7f);
}
