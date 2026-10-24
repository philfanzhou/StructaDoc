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
using StructaDoc.Host.Settings;

namespace StructaDoc.Host.Authentication;

public static class HostAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocHostAuthentication(
        this IServiceCollection services,
        StructaDocAuthenticationOptions options,
        AuthenticationTuningOptions tuningOptions,
        OidcAuthenticationOptions oidcOptions,
        IDataProtectionProvider keyRing,
        ServiceMantleBuilder serviceMantle)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tuningOptions);
        ArgumentNullException.ThrowIfNull(oidcOptions);
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(serviceMantle);
        options.Validate();
        tuningOptions.Validate();
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
        // instead of the previous 1–1000 / 1 s–1 h that only the local policy enforced. The values
        // arrive through `tuningOptions`, bound from the settings-aware configuration, and the
        // catalog's write-time bounds for the two keys sit inside that narrower range so a value a
        // browser can store is never one the next start refuses.
        serviceMantle
            .AddRateLimiting(rateLimiting =>
            {
                rateLimiting.Setup.PermitLimit = tuningOptions.LoginPermitLimit;
                rateLimiting.Setup.Window = tuningOptions.LoginRateLimitWindow;
                rateLimiting.Management.PermitLimit = tuningOptions.LoginPermitLimit;
                rateLimiting.Management.Window = tuningOptions.LoginRateLimitWindow;
            })
            .AddSecurityResponseHeaders();

        // The connection tests — OIDC discovery, the storage probe, the database probe — are the
        // settings endpoints that make the service reach an address the caller chose, so they get
        // their own named policy. Neither library policy can count by StructaDoc administrator:
        // `servicemantle.management` partitions by the library's own operator identity and consumer
        // policies by client address, so the policy is registered locally on the same
        // `RateLimiterOptions` the library configured. Riding that registration is what keeps a
        // refusal identical to sign-in's — the shared `OnRejected` answers with the same safe
        // `application/problem+json` body (`rate_limit.exceeded`, `Retry-After`, `correlationId`)
        // and logs the warning that is the visible trace a rejection leaves — while the partition
        // is the administrator's stable identity rather than the address. All three test endpoints
        // share the one bucket, with fixed limits rather than catalog keys, because the budget it
        // protects is the service's own outbound requests.
        services.AddConnectionTestRateLimiting();

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
                cookie.ExpireTimeSpan = tuningOptions.AdministratorSessionLifetime;
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
            // The one policy that does not admit an interactive session. Layout detection is an
            // API-client endpoint (ADR-0013 point 5): a machine caller holds the scope, while a
            // signed-in browser session has no business reaching a non-archiving detection API, so
            // the IsInteractive branch the four document/parse policies carry is deliberately
            // absent here — an administrator in a browser is refused exactly like anyone else.
            .AddPolicy(
                AuthorizationPolicies.LayoutDetect,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        HasScope(context.User, AuthenticationScopes.LayoutDetect)))
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
