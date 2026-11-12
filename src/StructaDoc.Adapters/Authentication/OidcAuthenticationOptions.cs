namespace StructaDoc.Adapters.Authentication;

public sealed class OidcAuthenticationOptions
{
    public const string SectionName = "Oidc";

    public bool Enabled { get; init; }
    public string Authority { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public string CallbackPath { get; init; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; init; } = "/signout-callback-oidc";

    /// <summary>
    /// The configured scope list. The default is deliberately empty rather than
    /// <c>openid profile email</c>: the configuration binder appends indexed values to a
    /// non-empty default array instead of replacing it, so a deployment's shorter list — SignaCore
    /// deployments in particular must not request <c>email</c>, which it does not support — could
    /// never take effect. An empty list means the documented default through
    /// <see cref="EffectiveScopes"/>.
    /// </summary>
    public string[] Scopes { get; init; } = [];

    /// <summary>The scopes a sign-in requests: the configured list, or the documented
    /// <c>openid profile email</c> default when nothing is configured.</summary>
    public string[] EffectiveScopes => Scopes.Length > 0 ? Scopes : ["openid", "profile", "email"];

    public string NameClaim { get; init; } = "name";
    public string EmailClaim { get; init; } = "email";
    public string RoleClaim { get; init; } = "role";
    public string AdministratorRole { get; init; } = "structadoc-admin";

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority)
            || authority.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("Oidc:Authority must be an absolute HTTP(S) URI.");
        }

        if (RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Oidc:Authority must use HTTPS when RequireHttpsMetadata is enabled.");
        }

        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 512)
        {
            throw new InvalidOperationException("Oidc:ClientId must be configured.");
        }

        if (string.IsNullOrWhiteSpace(CallbackPath) || !CallbackPath.StartsWith('/'))
        {
            throw new InvalidOperationException("Oidc:CallbackPath must be an application-relative path.");
        }

        var scopes = EffectiveScopes;
        if (scopes.Length == 0 || !scopes.Contains("openid", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Oidc:Scopes must contain openid.");
        }

        foreach (var value in new[] { NameClaim, EmailClaim, RoleClaim, AdministratorRole })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            {
                throw new InvalidOperationException("OIDC claim and role mappings must be non-empty and at most 255 characters.");
            }
        }
    }
}
