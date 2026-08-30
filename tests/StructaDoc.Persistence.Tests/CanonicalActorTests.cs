using System.Security.Claims;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Persistence.Tests;

public sealed class CanonicalActorTests
{
    [Fact]
    public void Maximum_oidc_actor_round_trips_ascii_bytes_including_nul_without_normalization()
    {
        const string issuerPrefix = "https://identity.example/";
        var issuer = issuerPrefix
            + new string('I', CanonicalActorPersistence.MaximumIssuerByteCount - issuerPrefix.Length);
        var subject = "CaseSensitive\0"
            + new string('s', CanonicalActorPersistence.MaximumSubjectByteCount - 14);
        var principal = CreatePrincipal(
            SubjectTypes.User,
            new(StructaDocClaimTypes.ExternalIssuer, issuer),
            new(StructaDocClaimTypes.ExternalSubject, subject));

        var actor = CanonicalActor.FromPrincipal(principal);
        var issuerBytes = actor.EncodeIssuer();
        var subjectBytes = actor.EncodeSubject();
        var restored = CanonicalActor.FromStoredBytes(issuerBytes, subjectBytes);

        Assert.Equal(CanonicalActorPersistence.MaximumIssuerByteCount, issuerBytes.Length);
        Assert.Equal(CanonicalActorPersistence.MaximumSubjectByteCount, subjectBytes.Length);
        Assert.Contains((byte)0, subjectBytes);
        Assert.Equal(issuer, actor.Issuer);
        Assert.Equal(subject, actor.Subject);
        Assert.Equal(actor, restored);
    }

    [Theory]
    [InlineData(SubjectTypes.ApiClient, PrincipalIdentity.ApiClientIssuer)]
    [InlineData(SubjectTypes.Administrator, CanonicalActor.AdministratorIssuer)]
    public void Uuid_backed_actor_subjects_are_normalized_to_lowercase_d_format(
        string subjectType,
        string expectedIssuer)
    {
        var subjectId = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
        var principal = CreatePrincipal(
            subjectType,
            new Claim(ClaimTypes.NameIdentifier, subjectId.ToString("D").ToUpperInvariant()));

        var actor = CanonicalActor.FromPrincipal(principal);

        Assert.Equal(expectedIssuer, actor.Issuer);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", actor.Subject);
    }

    [Fact]
    public void Oidc_administrator_role_still_maps_as_an_oidc_actor()
    {
        var principal = CreatePrincipal(
            SubjectTypes.User,
            new(StructaDocClaimTypes.ExternalIssuer, "https://identity.example/UPPER"),
            new(StructaDocClaimTypes.ExternalSubject, "MixedCaseSubject"),
            new(StructaDocClaimTypes.Administrator, bool.TrueString));

        var actor = CanonicalActor.FromPrincipal(principal);

        Assert.Equal("https://identity.example/UPPER", actor.Issuer);
        Assert.Equal("MixedCaseSubject", actor.Subject);
    }

    [Fact]
    public void Invalid_subject_type_has_an_explicit_error()
    {
        var principal = CreatePrincipal("service");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CanonicalActor.FromPrincipal(principal));

        Assert.Contains("subject type 'service' is not supported", exception.Message);
    }

    [Fact]
    public void Missing_subject_type_has_an_explicit_error()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CanonicalActor.FromPrincipal(principal));

        Assert.Equal("Authenticated subject type is missing.", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_oidc_pair_has_an_explicit_error(bool includeIssuer)
    {
        var claim = includeIssuer
            ? new Claim(StructaDocClaimTypes.ExternalIssuer, "https://identity.example")
            : new Claim(StructaDocClaimTypes.ExternalSubject, "subject");
        var principal = CreatePrincipal(SubjectTypes.User, claim);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CanonicalActor.FromPrincipal(principal));

        Assert.Contains("claim is missing", exception.Message);
    }

    [Fact]
    public void Oidc_value_outside_authentication_boundary_has_an_explicit_error()
    {
        var principal = CreatePrincipal(
            SubjectTypes.User,
            new(StructaDocClaimTypes.ExternalIssuer, "https://identity.example"),
            new(
                StructaDocClaimTypes.ExternalSubject,
                new string('s', CanonicalActorPersistence.MaximumSubjectByteCount + 1)));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CanonicalActor.FromPrincipal(principal));

        Assert.Equal("OIDC actor claims are invalid.", exception.Message);
        Assert.Contains("at most 255", exception.InnerException?.Message);
    }

    [Theory]
    [InlineData(PrincipalIdentity.ApiClientIssuer)]
    [InlineData(CanonicalActor.AdministratorIssuer)]
    public void Oidc_reserved_issuer_has_an_explicit_error(string issuer)
    {
        var principal = CreatePrincipal(
            SubjectTypes.User,
            new(StructaDocClaimTypes.ExternalIssuer, issuer),
            new(
                StructaDocClaimTypes.ExternalSubject,
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CanonicalActor.FromPrincipal(principal));

        Assert.Equal("OIDC actor claims are invalid.", exception.Message);
        Assert.Contains("accepted absolute HTTP(S)", exception.InnerException?.Message);
    }

    [Fact]
    public void Invalid_uuid_backed_subject_has_an_explicit_error()
    {
        var principal = CreatePrincipal(
            SubjectTypes.ApiClient,
            new Claim(ClaimTypes.NameIdentifier, "not-a-uuid"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CanonicalActor.FromPrincipal(principal));

        Assert.Contains("must be a D-format UUID", exception.Message);
    }

    [Fact]
    public void Ascii_codec_enforces_maximum_lengths_and_rejects_non_ascii_bytes()
    {
        var issuerException = Assert.Throws<ArgumentException>(() =>
            CanonicalActorPersistence.EncodeIssuer(
                new string('i', CanonicalActorPersistence.MaximumIssuerByteCount + 1)));
        var subjectException = Assert.Throws<ArgumentException>(() =>
            CanonicalActorPersistence.EncodeSubject(
                new string('s', CanonicalActorPersistence.MaximumSubjectByteCount + 1)));
        var storedLengthException = Assert.Throws<InvalidOperationException>(() =>
            CanonicalActorPersistence.DecodeSubject(
                new byte[CanonicalActorPersistence.MaximumSubjectByteCount + 1]));
        var nonAsciiException = Assert.Throws<InvalidOperationException>(() =>
            CanonicalActorPersistence.DecodeIssuer([0x80]));

        Assert.Contains("512 ASCII bytes", issuerException.Message);
        Assert.Contains("255 ASCII bytes", subjectException.Message);
        Assert.Contains("255-byte limit", storedLengthException.Message);
        Assert.Contains("is not ASCII", nonAsciiException.Message);
    }

    [Fact]
    public void Persisted_state_validation_distinguishes_canonical_legacy_and_empty_states()
    {
        var actor = CanonicalActor.Create(
            "https://identity.example",
            "subject\0with-nul");
        var legacyBytes = CanonicalActorPersistence.EncodeLegacy(
            "oidc:https://identity.example|subject\0with-nul",
            CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount);

        var canonicalState = CanonicalActorPersistence.ValidateState(
            actor.EncodeIssuer(),
            actor.EncodeSubject(),
            legacyBytes: null,
            CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount,
            allowEmpty: true);
        var legacyState = CanonicalActorPersistence.ValidateState(
            issuerBytes: null,
            subjectBytes: null,
            legacyBytes,
            CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount,
            allowEmpty: true);
        var emptyState = CanonicalActorPersistence.ValidateState(
            issuerBytes: null,
            subjectBytes: null,
            legacyBytes: null,
            CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount,
            allowEmpty: true);

        Assert.Equal(PersistedActorState.Canonical, canonicalState);
        Assert.Equal(PersistedActorState.Legacy, legacyState);
        Assert.Equal(PersistedActorState.Empty, emptyState);
        Assert.Equal(
            "oidc:https://identity.example|subject\0with-nul",
            CanonicalActorPersistence.DecodeLegacy(
                legacyBytes,
                CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount));
    }

    [Fact]
    public void Persisted_state_validation_rejects_missing_pair_mixed_and_forbidden_empty_states()
    {
        var actor = CanonicalActor.Create("https://identity.example", "subject");
        var issuerBytes = actor.EncodeIssuer();
        var subjectBytes = actor.EncodeSubject();
        byte[] legacyBytes = [1];

        var missingPair = Assert.Throws<InvalidOperationException>(() =>
            CanonicalActorPersistence.ValidateState(
                issuerBytes,
                subjectBytes: null,
                legacyBytes: null,
                CanonicalActorPersistence.MaximumAccessGrantLegacyByteCount,
                allowEmpty: false));
        var mixedState = Assert.Throws<InvalidOperationException>(() =>
            CanonicalActorPersistence.ValidateState(
                issuerBytes,
                subjectBytes,
                legacyBytes,
                CanonicalActorPersistence.MaximumAccessGrantLegacyByteCount,
                allowEmpty: false));
        var emptyState = Assert.Throws<InvalidOperationException>(() =>
            CanonicalActorPersistence.ValidateState(
                issuerBytes: null,
                subjectBytes: null,
                legacyBytes: null,
                CanonicalActorPersistence.MaximumAccessGrantLegacyByteCount,
                allowEmpty: false));

        Assert.Contains("present or absent together", missingPair.Message);
        Assert.Contains("cannot both be present", mixedState.Message);
        Assert.Contains("cannot be empty", emptyState.Message);
    }

    [Fact]
    public void Persisted_state_validation_rejects_noncanonical_uuid_case()
    {
        var issuerBytes = CanonicalActorPersistence.EncodeIssuer(PrincipalIdentity.ApiClientIssuer);
        var subjectBytes = CanonicalActorPersistence.EncodeSubject(
            "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CanonicalActorPersistence.ValidateState(
                issuerBytes,
                subjectBytes,
                legacyBytes: null,
                CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount,
                allowEmpty: false));

        Assert.Contains("lowercase D-format", exception.Message);
    }

    [Fact]
    public void Legacy_codec_rejects_invalid_utf8_unicode_and_oversized_values()
    {
        var invalidUnicode = Assert.Throws<ArgumentException>(() =>
            CanonicalActorPersistence.EncodeLegacy(
                "\ud800",
                CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount));
        var invalidUtf8 = Assert.Throws<InvalidOperationException>(() =>
            CanonicalActorPersistence.DecodeLegacy(
                [0xff],
                CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount));
        var oversized = Assert.Throws<ArgumentException>(() =>
            CanonicalActorPersistence.EncodeLegacy(
                new string('a', CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount + 1),
                CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount));

        Assert.Contains("invalid Unicode", invalidUnicode.Message);
        Assert.Contains("not valid strict UTF-8", invalidUtf8.Message);
        Assert.Contains("cannot exceed 1024 UTF-8 bytes", oversized.Message);
    }

    [Theory]
    [InlineData(CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount)]
    [InlineData(CanonicalActorPersistence.MaximumAccessGrantLegacyByteCount)]
    public void Legacy_codec_round_trips_each_maximum_byte_domain(int maximumByteCount)
    {
        var legacyActor = new string('a', maximumByteCount);

        var bytes = CanonicalActorPersistence.EncodeLegacy(legacyActor, maximumByteCount);
        var restored = CanonicalActorPersistence.DecodeLegacy(bytes, maximumByteCount);

        Assert.Equal(maximumByteCount, bytes.Length);
        Assert.Equal(legacyActor, restored);
    }

    private static ClaimsPrincipal CreatePrincipal(string subjectType, params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(StructaDocClaimTypes.SubjectType, subjectType), .. claims],
            authenticationType: "test"));
    }
}
