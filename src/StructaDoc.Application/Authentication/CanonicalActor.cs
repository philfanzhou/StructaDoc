using System.Security.Claims;
using System.Text;

namespace StructaDoc.Application.Authentication;

public sealed record CanonicalActor
{
    public const string AdministratorIssuer = "structadoc:administrator";

    private CanonicalActor(string issuer, string subject)
    {
        Issuer = issuer;
        Subject = subject;
    }

    public string Issuer { get; }

    public string Subject { get; }

    public static CanonicalActor FromPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subjectType = principal.FindFirst(StructaDocClaimTypes.SubjectType)?.Value
            ?? throw new InvalidOperationException("Authenticated subject type is missing.");
        return subjectType switch
        {
            SubjectTypes.User => FromOidcPrincipal(principal),
            SubjectTypes.ApiClient => FromUuidPrincipal(
                principal,
                PrincipalIdentity.ApiClientIssuer,
                "API-client"),
            SubjectTypes.Administrator => FromUuidPrincipal(
                principal,
                AdministratorIssuer,
                "Local-administrator"),
            _ => throw new InvalidOperationException(
                $"Authenticated subject type '{subjectType}' is not supported."),
        };
    }

    public static CanonicalActor Create(string issuer, string subject)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(subject);

        if (string.Equals(issuer, PrincipalIdentity.ApiClientIssuer, StringComparison.Ordinal)
            || string.Equals(issuer, AdministratorIssuer, StringComparison.Ordinal))
        {
            if (!Guid.TryParseExact(subject, "D", out var subjectId))
            {
                throw new ArgumentException(
                    $"Subject for reserved issuer '{issuer}' must be a D-format UUID.",
                    nameof(subject));
            }

            return new CanonicalActor(issuer, subjectId.ToString("D"));
        }

        return CreateOidc(issuer, subject);
    }

    public static CanonicalActor FromStoredBytes(
        ReadOnlySpan<byte> issuerBytes,
        ReadOnlySpan<byte> subjectBytes)
    {
        var issuer = CanonicalActorPersistence.DecodeIssuer(issuerBytes);
        var subject = CanonicalActorPersistence.DecodeSubject(subjectBytes);

        CanonicalActor actor;
        try
        {
            actor = Create(issuer, subject);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Stored canonical actor issuer and subject do not form a valid actor.",
                exception);
        }

        if (!string.Equals(actor.Subject, subject, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Stored UUID-backed actor subject must use lowercase D-format.");
        }

        return actor;
    }

    public byte[] EncodeIssuer() => CanonicalActorPersistence.EncodeIssuer(Issuer);

    public byte[] EncodeSubject() => CanonicalActorPersistence.EncodeSubject(Subject);

    private static CanonicalActor FromOidcPrincipal(ClaimsPrincipal principal)
    {
        var issuer = principal.FindFirst(StructaDocClaimTypes.ExternalIssuer)?.Value
            ?? throw new InvalidOperationException("OIDC actor issuer claim is missing.");
        var subject = principal.FindFirst(StructaDocClaimTypes.ExternalSubject)?.Value
            ?? throw new InvalidOperationException("OIDC actor subject claim is missing.");

        try
        {
            return CreateOidc(issuer, subject);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("OIDC actor claims are invalid.", exception);
        }
    }

    private static CanonicalActor CreateOidc(string issuer, string subject)
    {
        if (!ExternalIdentityConstraints.IsValidIssuer(issuer))
        {
            throw new ArgumentException(
                "OIDC actor issuer must be an accepted absolute HTTP(S) ASCII issuer.",
                nameof(issuer));
        }

        if (!ExternalIdentityConstraints.IsValidSubject(subject))
        {
            throw new ArgumentException(
                "OIDC actor subject must be accepted ASCII text of at most 255 characters.",
                nameof(subject));
        }

        return new CanonicalActor(issuer, subject);
    }

    private static CanonicalActor FromUuidPrincipal(
        ClaimsPrincipal principal,
        string issuer,
        string actorName)
    {
        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException($"{actorName} subject ID claim is missing.");
        if (!Guid.TryParseExact(subject, "D", out var subjectId))
        {
            throw new InvalidOperationException(
                $"{actorName} subject ID claim must be a D-format UUID.");
        }

        return new CanonicalActor(issuer, subjectId.ToString("D"));
    }
}

public enum PersistedActorState
{
    Empty,
    Canonical,
    Legacy,
}

public static class CanonicalActorPersistence
{
    public const int MaximumIssuerByteCount = ExternalIdentityConstraints.MaximumIssuerLength;
    public const int MaximumSubjectByteCount = ExternalIdentityConstraints.MaximumSubjectLength;
    public const int MaximumDocumentOrParseRunLegacyByteCount = 1024;
    public const int MaximumAccessGrantLegacyByteCount = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] EncodeIssuer(string issuer) =>
        EncodeAscii(issuer, MaximumIssuerByteCount, "Canonical actor issuer");

    public static byte[] EncodeSubject(string subject) =>
        EncodeAscii(subject, MaximumSubjectByteCount, "Canonical actor subject");

    public static string DecodeIssuer(ReadOnlySpan<byte> issuerBytes) =>
        DecodeAscii(issuerBytes, MaximumIssuerByteCount, "Canonical actor issuer");

    public static string DecodeSubject(ReadOnlySpan<byte> subjectBytes) =>
        DecodeAscii(subjectBytes, MaximumSubjectByteCount, "Canonical actor subject");

    public static byte[] EncodeLegacy(string legacyActor, int maximumByteCount)
    {
        ArgumentNullException.ThrowIfNull(legacyActor);
        ValidateMaximumByteCount(maximumByteCount);

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(legacyActor);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Legacy actor contains invalid Unicode and cannot be encoded as strict UTF-8.",
                nameof(legacyActor),
                exception);
        }

        if (bytes.Length > maximumByteCount)
        {
            throw new ArgumentException(
                $"Legacy actor cannot exceed {maximumByteCount} UTF-8 bytes.",
                nameof(legacyActor));
        }

        return bytes;
    }

    public static string DecodeLegacy(ReadOnlySpan<byte> legacyBytes, int maximumByteCount)
    {
        ValidateMaximumByteCount(maximumByteCount);
        if (legacyBytes.Length > maximumByteCount)
        {
            throw new InvalidOperationException(
                $"Stored legacy actor exceeds the {maximumByteCount}-byte limit.");
        }

        try
        {
            return StrictUtf8.GetString(legacyBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                "Stored legacy actor is not valid strict UTF-8.",
                exception);
        }
    }

    public static PersistedActorState ValidateState(
        byte[]? issuerBytes,
        byte[]? subjectBytes,
        byte[]? legacyBytes,
        int maximumLegacyByteCount,
        bool allowEmpty)
    {
        ValidateMaximumByteCount(maximumLegacyByteCount);

        if ((issuerBytes is null) != (subjectBytes is null))
        {
            throw new InvalidOperationException(
                "Canonical actor issuer and subject must be present or absent together.");
        }

        if (issuerBytes is not null)
        {
            if (legacyBytes is not null)
            {
                throw new InvalidOperationException(
                    "Canonical actor fields and the legacy actor field cannot both be present.");
            }

            _ = CanonicalActor.FromStoredBytes(issuerBytes, subjectBytes!);
            return PersistedActorState.Canonical;
        }

        if (legacyBytes is not null)
        {
            _ = DecodeLegacy(legacyBytes, maximumLegacyByteCount);
            return PersistedActorState.Legacy;
        }

        if (!allowEmpty)
        {
            throw new InvalidOperationException("Persisted actor state cannot be empty.");
        }

        return PersistedActorState.Empty;
    }

    private static byte[] EncodeAscii(string value, int maximumByteCount, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > maximumByteCount)
        {
            throw new ArgumentException(
                $"{fieldName} cannot exceed {maximumByteCount} ASCII bytes.",
                nameof(value));
        }

        var bytes = new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character > 0x7f)
            {
                throw new ArgumentException($"{fieldName} must contain only ASCII bytes.", nameof(value));
            }

            bytes[index] = (byte)character;
        }

        return bytes;
    }

    private static string DecodeAscii(
        ReadOnlySpan<byte> bytes,
        int maximumByteCount,
        string fieldName)
    {
        if (bytes.Length > maximumByteCount)
        {
            throw new InvalidOperationException(
                $"Stored {fieldName.ToLowerInvariant()} exceeds the {maximumByteCount}-byte limit.");
        }

        return string.Create(bytes.Length, bytes.ToArray(), (characters, state) =>
        {
            for (var index = 0; index < state.Length; index++)
            {
                var value = state[index];
                if (value > 0x7f)
                {
                    throw new InvalidOperationException($"Stored {fieldName.ToLowerInvariant()} is not ASCII.");
                }

                characters[index] = (char)value;
            }
        });
    }

    private static void ValidateMaximumByteCount(int maximumByteCount)
    {
        if (maximumByteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumByteCount),
                maximumByteCount,
                "Maximum byte count must be positive.");
        }
    }
}
