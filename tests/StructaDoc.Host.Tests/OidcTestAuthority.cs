using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace StructaDoc.Host.Tests;

// In-process HTTP authority emulating a standard OIDC provider (discovery, JWKS, and the token
// endpoint) for the interactive sign-in tests: no production network, no credentials, and a
// throwaway signing key. SignaCore's own discovery shape — including the standard
// end_session_endpoint the RP-initiated logout reads — is what the document below mirrors, so the
// tests exercise the same wire contract a real standards-compliant provider answers with.
internal sealed class OidcTestAuthority : HttpMessageHandler
{
    internal const string Issuer = "https://identity.example.test";
    internal const string ClientId = "structadoc-test-client";
    internal const string ClientSecret = "fictitious-client-secret-canary";
    internal const string AdministratorRole = "structadoc-admin";
    internal const string SubjectId = "subject-0001";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, (string Nonce, string Challenge, string? Role)> _codes = [];

    /// <summary>Set to fail the next discovery retrieval, the way an unreachable authority does.</summary>
    internal bool DiscoveryFails { get; set; }

    internal bool HasEndSessionEndpoint { get; init; } = true;

    /// <summary>
    /// Profile claims the minted ID tokens carry instead of the default <c>name</c> claim: the
    /// shape of an authority that writes its profile claims under non-standard names, which is
    /// what the configured claim mappings exist for.
    /// </summary>
    internal Dictionary<string, object?>? ProfileIdTokenClaims { get; set; }

    internal readonly ConcurrentQueue<Dictionary<string, string>> TokenForms = [];

    internal string? LastIdToken { get; private set; }

    /// <summary>
    /// Mints an authorization code for a handshake's query. The authorize endpoint itself is a
    /// browser route a real provider owns; the tests hold the redirect and present this code at
    /// the callback, exactly as the provider's own redirect would. The subject it redeems to
    /// carries the configured administrator role.
    /// </summary>
    internal string Code(IDictionary<string, string> query) => CodeForRole(query, AdministratorRole);

    /// <summary>As <see cref="Code"/>, choosing the role claim the redeemed ID token carries; a
    /// null role mints a subject without any role claim.</summary>
    internal string CodeForRole(IDictionary<string, string> query, string? role)
    {
        var code = "fictitious-code-" + Guid.NewGuid().ToString("N");
        _codes[code] = (query["nonce"], query["code_challenge"], role);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
        {
            if (DiscoveryFails)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return Json(JsonSerializer.Serialize(new
            {
                issuer = Issuer,
                jwks_uri = Issuer + "/.well-known/jwks",
                authorization_endpoint = Issuer + "/oauth2/authorize",
                token_endpoint = Issuer + "/oauth2/token",
                userinfo_endpoint = Issuer + "/oauth2/userinfo",
                end_session_endpoint = HasEndSessionEndpoint ? Issuer + "/oauth2/session/end" : null,
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                scopes_supported = new[] { "openid", "profile" },
                code_challenge_methods_supported = new[] { "S256" },
            }, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
        }

        if (request.RequestUri.AbsolutePath == "/.well-known/jwks")
        {
            var key = Microsoft.IdentityModel.Tokens.JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new Microsoft.IdentityModel.Tokens.RsaSecurityKey(_rsa) { KeyId = "test-kid" });
            return Json(JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = "RSA", kid = "test-kid", use = "sig", alg = "RS256", n = key.N, e = key.E } },
            }));
        }

        if (request.RequestUri.AbsolutePath == "/oauth2/userinfo")
        {
            // The scope-controlled UserInfo read the handler requests when it is enabled; it
            // answers with exactly the identity the ID token carries.
            return Json(JsonSerializer.Serialize(new { sub = SubjectId, name = "Test Identity" }));
        }

        if (request.RequestUri.AbsolutePath == "/oauth2/token")
        {
            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = QueryHelpers.ParseQuery(raw).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            TokenForms.Enqueue(form);
            if (!_codes.TryRemove(form["code"], out var handshake))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            if (WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])))
                != handshake.Challenge)
            {
                throw new InvalidOperationException("fake.pkce_mismatch");
            }

            LastIdToken = MintIdToken(handshake.Nonce, handshake.Role);
            return Json(JsonSerializer.Serialize(new
            {
                access_token = "fictitious-access-token-canary",
                token_type = "Bearer",
                expires_in = 900,
                id_token = LastIdToken,
            }));
        }

        throw new InvalidOperationException("fake.unexpected_endpoint:" + request.RequestUri.AbsolutePath);
    }

    // The ID token carries the claims StructaDoc maps: sub and name as the identity, role as the
    // administrator-mapping input. A null role mints a subject with no role claim at all.
    private string MintIdToken(string nonce, string? role)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid = "test-kid" });
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = Issuer,
            ["aud"] = ClientId,
            ["sub"] = SubjectId,
            ["iat"] = now,
            ["exp"] = now + 300,
            ["nonce"] = nonce,
        };
        if (ProfileIdTokenClaims is null)
        {
            payload["name"] = "Test Identity";
        }
        else
        {
            foreach (var (claim, value) in ProfileIdTokenClaims)
            {
                payload[claim] = value;
            }
        }

        if (role is not null)
        {
            payload["role"] = role;
        }

        var input = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(header))
            + "." + WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return input + "." + WebEncoders.Base64UrlEncode(
            _rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
        }

        base.Dispose(disposing);
    }
}
