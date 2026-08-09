using System.Security.Cryptography;

namespace StructaDoc.Adapters.Authentication;

public static class ApiKeyCredential
{
    private const string VersionPrefix = "sd1";
    private const int SecretSize = 32;

    public static IssuedApiKey Create(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("API client ID cannot be empty.", nameof(clientId));
        }

        var secret = RandomNumberGenerator.GetBytes(SecretSize);
        var encodedSecret = Convert.ToBase64String(secret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var credential = $"{VersionPrefix}.{clientId:N}.{encodedSecret}";
        return new IssuedApiKey(
            clientId,
            credential,
            SHA256.HashData(secret));
    }

    public static bool TryParse(
        string credential,
        out Guid clientId,
        out byte[] secretHash)
    {
        clientId = Guid.Empty;
        secretHash = [];

        if (string.IsNullOrWhiteSpace(credential))
        {
            return false;
        }

        var parts = credential.Split('.', StringSplitOptions.None);

        if (parts.Length != 3
            || !string.Equals(parts[0], VersionPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[1], "N", out clientId)
            || clientId == Guid.Empty
            || parts[2].Length != 43
            || parts[2].Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        try
        {
            var base64 = parts[2].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var secret = Convert.FromBase64String(base64);

            if (secret.Length != SecretSize)
            {
                clientId = Guid.Empty;
                return false;
            }

            secretHash = SHA256.HashData(secret);
            return true;
        }
        catch (FormatException)
        {
            clientId = Guid.Empty;
            secretHash = [];
            return false;
        }
    }
}

public sealed record IssuedApiKey(
    Guid ClientId,
    string Credential,
    byte[] SecretHash);
