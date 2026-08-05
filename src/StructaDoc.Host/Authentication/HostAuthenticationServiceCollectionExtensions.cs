using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Providers;
using StructaDoc.Infrastructure.Authentication;

namespace StructaDoc.Host.Authentication;

public static class HostAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddStructaDocHostAuthentication(
        this IServiceCollection services,
        StructaDocAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var keyPath = Path.GetFullPath(options.DataProtectionKeysPath);
        Directory.CreateDirectory(keyPath);

        services.AddStructaDocAuthenticationPersistence();
        services.AddDataProtection()
            .SetApplicationName("StructaDoc")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        services.AddSingleton<IProviderSecretProtector, DataProtectionProviderSecretProtector>();
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
        services
            .AddAuthentication(authentication =>
            {
                authentication.DefaultAuthenticateScheme = AuthenticationSchemes.Selector;
                authentication.DefaultChallengeScheme = AuthenticationSchemes.Selector;
                authentication.DefaultForbidScheme = AuthenticationSchemes.Selector;
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
                cookie.Cookie.Name = "StructaDoc.Administrator";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = options.AdministratorSessionLifetime;
                cookie.SlidingExpiration = true;
                cookie.EventsType = typeof(AdministratorCookieEvents);
            })
            .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                AuthenticationSchemes.ApiKey,
                configureOptions: null);

        services.AddAuthorizationBuilder()
            .AddPolicy(
                AuthorizationPolicies.Administrator,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(
                        StructaDocClaimTypes.SubjectType,
                        SubjectTypes.Administrator))
            .AddPolicy(
                AuthorizationPolicies.DocumentsRead,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.User.HasClaim(
                            StructaDocClaimTypes.SubjectType,
                            SubjectTypes.Administrator)
                        || context.User.HasClaim(
                            StructaDocClaimTypes.Scope,
                            AuthenticationScopes.DocumentsRead)))
            .AddPolicy(
                AuthorizationPolicies.DocumentsWrite,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.User.HasClaim(
                            StructaDocClaimTypes.SubjectType,
                            SubjectTypes.Administrator)
                        || context.User.HasClaim(
                            StructaDocClaimTypes.Scope,
                            AuthenticationScopes.DocumentsWrite)))
            .AddPolicy(
                AuthorizationPolicies.ParsesRead,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.User.HasClaim(
                            StructaDocClaimTypes.SubjectType,
                            SubjectTypes.Administrator)
                        || context.User.HasClaim(
                            StructaDocClaimTypes.Scope,
                            AuthenticationScopes.ParsesRead)))
            .AddPolicy(
                AuthorizationPolicies.ParsesWrite,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.User.HasClaim(
                            StructaDocClaimTypes.SubjectType,
                            SubjectTypes.Administrator)
                        || context.User.HasClaim(
                            StructaDocClaimTypes.Scope,
                            AuthenticationScopes.ParsesWrite)));

        return services;
    }
}
