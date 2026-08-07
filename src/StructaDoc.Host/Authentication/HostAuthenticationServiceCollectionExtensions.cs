using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Providers;
using StructaDoc.Infrastructure.Authentication;

namespace StructaDoc.Host.Authentication;

public static class HostAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocHostAuthentication(
        this IServiceCollection services,
        StructaDocAuthenticationOptions options,
        OidcAuthenticationOptions oidcOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(oidcOptions);
        options.Validate();
        oidcOptions.Validate();

        var keyPath = Path.GetFullPath(options.DataProtectionKeysPath);
        Directory.CreateDirectory(keyPath);

        services.AddStructaDocAuthenticationPersistence();
        services.AddDataProtection()
            .SetApplicationName("StructaDoc")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
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
        services.AddRateLimiter(rateLimiter =>
        {
            rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiter.AddPolicy(
                AuthorizationPolicies.AdministratorLoginRateLimit,
                context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = options.LoginPermitLimit,
                        QueueLimit = 0,
                        Window = options.LoginRateLimitWindow,
                    }));
        });

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
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException("OIDC identity is missing issuer or subject.");
        }

        identity.AddClaim(new Claim(StructaDocClaimTypes.SubjectType, SubjectTypes.User));
        identity.AddClaim(new Claim(StructaDocClaimTypes.ExternalIssuer, issuer));
        identity.AddClaim(new Claim(StructaDocClaimTypes.ExternalSubject, subject));
        if (!principal.HasClaim(ClaimTypes.NameIdentifier, subject))
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
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
