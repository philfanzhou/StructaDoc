using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Infrastructure.Authentication;

namespace StructaDoc.Host.Authentication;

public static class InteractiveSessionEndpoints
{
    public static IEndpointRouteBuilder MapInteractiveSessionEndpoints(
        this IEndpointRouteBuilder endpoints,
        OidcAuthenticationOptions oidcOptions)
    {
        var group = endpoints.MapGroup("/api/v1/session");
        group.MapGet("", (ClaimsPrincipal user) => Results.Ok(ToResponse(user, oidcOptions.Enabled)))
            .AllowAnonymous()
            .Produces<InteractiveSessionResponse>();
        group.MapGet("/login", (HttpContext context, string? returnUrl) =>
            BeginLogin(context, oidcOptions, returnUrl))
            .AllowAnonymous();
        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(AuthorizationPolicies.InteractiveUser)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return endpoints;
    }

    private static IResult BeginLogin(
        HttpContext context,
        OidcAuthenticationOptions options,
        string? returnUrl)
    {
        if (!options.Enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "OIDC authentication is disabled");
        }

        var destination = NormalizeReturnUrl(returnUrl);
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = destination },
            [AuthenticationSchemes.OpenIdConnect]);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        OidcAuthenticationOptions options)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Antiforgery validation failed");
        }

        await context.SignOutAsync(AuthenticationSchemes.AdministratorCookie);
        if (options.Enabled
            && context.User.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.User))
        {
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [AuthenticationSchemes.OpenIdConnect]);
        }

        return Results.NoContent();
    }

    private static InteractiveSessionResponse ToResponse(
        ClaimsPrincipal user,
        bool oidcEnabled)
    {
        var authenticated = user.Identity?.IsAuthenticated == true;
        return new InteractiveSessionResponse(
            authenticated,
            authenticated ? user.FindFirstValue(StructaDocClaimTypes.SubjectType) : null,
            authenticated
                ? user.FindFirstValue(StructaDocClaimTypes.ExternalSubject)
                    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                : null,
            authenticated ? user.FindFirstValue(StructaDocClaimTypes.ExternalIssuer) : null,
            authenticated ? user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue("name") : null,
            authenticated ? user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") : null,
            authenticated && (user.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.Administrator)
                || user.HasClaim(StructaDocClaimTypes.Administrator, bool.TrueString)),
            oidcEnabled);
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(returnUrl, UriKind.Absolute, out _))
        {
            return "/";
        }

        return returnUrl;
    }
}
