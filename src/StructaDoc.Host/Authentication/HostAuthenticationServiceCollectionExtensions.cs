using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ServiceMantle.Web;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Providers;

namespace StructaDoc.Host.Authentication;

public static class HostAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocHostAuthentication(
        this IServiceCollection services,
        StructaDocAuthenticationOptions options,
        OidcAuthenticationOptions oidcOptions,
        IDataProtectionProvider keyRing,
        ServiceMantleBuilder serviceMantle)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(oidcOptions);
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(serviceMantle);
        options.Validate();
        oidcOptions.Validate();

        services.AddStructaDocAuthenticationPersistence();

        // The key ring is supplied rather than configured here because one of the stored settings is
        // encrypted and has to be read before this container exists. Registering the same instance
        // keeps the process to a single key ring: two readers of one directory can each decide it is
        // empty on a first start and create a key the other has not cached.
        services.AddSingleton(keyRing);
        services.AddSingleton<IProviderSecretProtector, DataProtectionProviderSecretProtector>();
        services.AddSingleton<IProviderSubmissionProtector, DataProtectionProviderSubmissionProtector>();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = "X-CSRF-TOKEN";
            antiforgery.Cookie.Name = "StructaDoc.Antiforgery";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        // Rate limiting comes from ServiceMantle's two isolated named policies rather than a local
        // one. `servicemantle.setup` (first-run claim) and `servicemantle.management` (administrator
        // sign-in) partition independently — an attacker exhausting one never consumes the other's
        // quota — and both read the same existing keys, so `Authentication:LoginPermitLimit` and
        // `Authentication:LoginRateLimitWindow` stay the only knobs. Three behaviors change by
        // adopting the library policies, all of them intentional tightenings: the window is sliding
        // rather than fixed, a rejected request is answered with the library's safe
        // `application/problem+json` body (`rate_limit.exceeded`, `Retry-After`, `correlationId`)
        // rather than an empty 429, and the shared keys must now satisfy the narrower of the two
        // policies — PermitLimit 1–60 and a 10 s–10 min window — or the host refuses to start,
        // instead of the previous 1–1000 / 1 s–1 h that only the local policy enforced.
        serviceMantle
            .AddRateLimiting(rateLimiting =>
            {
                rateLimiting.Setup.PermitLimit = options.LoginPermitLimit;
                rateLimiting.Setup.Window = options.LoginRateLimitWindow;
                rateLimiting.Management.PermitLimit = options.LoginPermitLimit;
                rateLimiting.Management.Window = options.LoginRateLimitWindow;
            })
            .AddSecurityResponseHeaders();

        services.AddScoped<AdministratorCookieEvents>();
        var authentication = services
            .AddAuthentication(authenticationOptions =>
            {
                authenticationOptions.DefaultAuthenticateScheme = AuthenticationSchemes.Selector;
                authenticationOptions.DefaultChallengeScheme = AuthenticationSchemes.Selector;
                authenticationOptions.DefaultForbidScheme = AuthenticationSchemes.Selector;
            })
            .AddPolicyScheme(
                AuthenticationSchemes.Selector,
                displayName: null,
                selector => selector.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString()
                        .StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase)
                        ? AuthenticationSchemes.ApiKey
                        : AuthenticationSchemes.AdministratorCookie)
            .AddCookie(AuthenticationSchemes.AdministratorCookie, cookie =>
            {
                cookie.Cookie.Name = "StructaDoc.Interactive";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = options.AdministratorSessionLifetime;
                cookie.SlidingExpiration = true;
                cookie.EventsType = typeof(AdministratorCookieEvents);
            })
            .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                AuthenticationSchemes.ApiKey,
                configureOptions: null);

        if (oidcOptions.Enabled)
        {
            authentication.AddOpenIdConnect(AuthenticationSchemes.OpenIdConnect, oidc =>
            {
                oidc.Authority = oidcOptions.Authority.TrimEnd('/');
                oidc.ClientId = oidcOptions.ClientId;
                oidc.ClientSecret = oidcOptions.ClientSecret;
                oidc.RequireHttpsMetadata = oidcOptions.RequireHttpsMetadata;
                oidc.CallbackPath = oidcOptions.CallbackPath;
                oidc.SignedOutCallbackPath = oidcOptions.SignedOutCallbackPath;
                oidc.SignInScheme = AuthenticationSchemes.AdministratorCookie;
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.MapInboundClaims = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    NameClaimType = oidcOptions.NameClaim,
                    RoleClaimType = oidcOptions.RoleClaim,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                oidc.Scope.Clear();
                foreach (var scope in oidcOptions.Scopes.Distinct(StringComparer.Ordinal))
                {
                    oidc.Scope.Add(scope);
                }

                oidc.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = context =>
                    {
                        NormalizeOidcPrincipal(
                            context.Principal,
                            context.SecurityToken.Issuer,
                            oidcOptions);
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        context.HandleResponse();
                        context.Response.Redirect("/?authentication=failed");
                        return Task.CompletedTask;
                    },
                };
            });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(
                AuthorizationPolicies.Administrator,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => IsAdministrator(context.User)))
            .AddPolicy(
                AuthorizationPolicies.DocumentsRead,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        IsInteractive(context.User)
                        || HasScope(context.User, AuthenticationScopes.DocumentsRead)))
            .AddPolicy(
                AuthorizationPolicies.DocumentsWrite,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        IsInteractive(context.User)
                        || HasScope(context.User, AuthenticationScopes.DocumentsWrite)))
            .AddPolicy(
                AuthorizationPolicies.ParsesRead,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        IsInteractive(context.User)
                        || HasScope(context.User, AuthenticationScopes.ParsesRead)))
            .AddPolicy(
                AuthorizationPolicies.ParsesWrite,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        IsInteractive(context.User)
                        || HasScope(context.User, AuthenticationScopes.ParsesWrite)))
            .AddPolicy(
                AuthorizationPolicies.InteractiveUser,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => IsInteractive(context.User)));

        return services;
    }

    private static bool IsInteractive(ClaimsPrincipal principal) =>
        principal.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.Administrator)
        || principal.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.User);

    private static bool IsAdministrator(ClaimsPrincipal principal) =>
        principal.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.Administrator)
        || principal.HasClaim(StructaDocClaimTypes.Administrator, bool.TrueString);

    private static bool HasScope(ClaimsPrincipal principal, string scope) =>
        principal.HasClaim(StructaDocClaimTypes.Scope, scope);

    private static void NormalizeOidcPrincipal(
        ClaimsPrincipal? principal,
        string tokenIssuer,
        OidcAuthenticationOptions options)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            throw new InvalidOperationException("OIDC did not produce an authenticated claims identity.");
        }

        var issuer = principal.FindFirst("iss")?.Value ?? tokenIssuer;
        var subject = principal.FindFirst("sub")?.Value;
        if (!ExternalIdentityConstraints.IsValidIssuer(issuer)
            || !ExternalIdentityConstraints.IsValidSubject(subject))
        {
            throw new InvalidOperationException("OIDC identity contains an invalid issuer or subject.");
        }

        var normalizedSubject = subject!;
        identity.AddClaim(new Claim(StructaDocClaimTypes.SubjectType, SubjectTypes.User));
        identity.AddClaim(new Claim(StructaDocClaimTypes.ExternalIssuer, issuer));
        identity.AddClaim(new Claim(StructaDocClaimTypes.ExternalSubject, normalizedSubject));
        if (!principal.HasClaim(ClaimTypes.NameIdentifier, normalizedSubject))
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, normalizedSubject));
        }

        var isAdministrator = principal.Claims.Any(claim =>
            string.Equals(claim.Type, options.RoleClaim, StringComparison.Ordinal)
            && string.Equals(claim.Value, options.AdministratorRole, StringComparison.OrdinalIgnoreCase));
        if (isAdministrator)
        {
            identity.AddClaim(new Claim(StructaDocClaimTypes.Administrator, bool.TrueString));
        }
    }
}
