using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Authentication;

public static class InteractiveSessionEndpoints
{
    public static IEndpointRouteBuilder MapInteractiveSessionEndpoints(
        this IEndpointRouteBuilder endpoints,
        OidcAuthenticationOptions oidcOptions)
    {
        var group = endpoints.MapGroup("/api/v1/session");
        group.MapGet(
                "",
                async (
                    ClaimsPrincipal user,
                    IAdministratorProvisioningService provisioning,
                    CancellationToken cancellationToken) => Results.Ok(ToResponse(
                    user,
                    oidcOptions,
                    !await provisioning.AnyAdministratorExistsAsync(cancellationToken))))
            .AllowAnonymous()
            .Produces<InteractiveSessionResponse>();
        // A redirect is the whole answer here: the browser leaves for the identity provider and
        // comes back signed in. Undeclared, the one operation that returns no body at all reads
        // like the ones that forgot to say.
        group.MapGet("/login", (HttpContext context, string? returnUrl) =>
            OidcSignInFlow.BeginSignInAsync(context, oidcOptions, returnUrl, administratorEntry: false, "/"))
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(AuthorizationPolicies.InteractiveUser)
            .WithDescription("Ends the interactive session. A browser form can submit the antiforgery token "
                + "as `__RequestVerificationToken`. For an OIDC session with an end-session endpoint, "
                + "the response redirects there. For local-only sign-out, set `redirectToHome=true` "
                + "to redirect to `/`; otherwise the response is `204`.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return endpoints;
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        OidcAuthenticationOptions options,
        bool? redirectToHome)
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

        // The upstream sign-out runs first, while the cookie ticket still holds the tokens saved
        // at sign-in: the end-session redirect carries the stored ID token as `id_token_hint`,
        // which is how SignaCore and other authorities resolve which application is signing out
        // before they honor the registered post-logout redirect. The local session is deleted
        // afterwards, on the same response the redirect is already the answer of.
        var remoteSignOut = false;
        if (options.Enabled
            && context.User.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.User))
        {
            try
            {
                await context.SignOutAsync(
                    AuthenticationSchemes.OpenIdConnect,
                    new AuthenticationProperties { RedirectUri = "/" });
                remoteSignOut = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // An authority that publishes no usable end-session endpoint cannot be handed
                // the browser. Continue with local sign-out.
            }
        }

        await context.SignOutAsync(AuthenticationSchemes.AdministratorCookie);
        if (remoteSignOut)
        {
            return Results.Empty;
        }

        // The SPA opts into a navigation even when sign-out finishes on this origin.
        return redirectToHome == true ? Results.Redirect("/") : Results.NoContent();
    }

    private static InteractiveSessionResponse ToResponse(
        ClaimsPrincipal user,
        OidcAuthenticationOptions oidcOptions,
        bool setupRequired)
    {
        var authenticated = user.Identity?.IsAuthenticated == true;
        // The display name and email of an identity-provider session follow the configured claim
        // mapping; every other principal — a local administrator, an API client — keeps its own
        // claims untouched, so the OIDC configuration never renames what those sessions report.
        var isOidcUser = user.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.User);
        return new InteractiveSessionResponse(
            authenticated,
            authenticated ? user.FindFirstValue(StructaDocClaimTypes.SubjectType) : null,
            authenticated
                ? user.FindFirstValue(StructaDocClaimTypes.ExternalSubject)
                    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                : null,
            authenticated ? user.FindFirstValue(StructaDocClaimTypes.ExternalIssuer) : null,
            authenticated
                ? (isOidcUser
                    ? ProfileClaim(user, oidcOptions.NameClaim, ClaimTypes.Name, "name")
                    : user.FindFirstValue(ClaimTypes.Name))
                : null,
            authenticated
                ? (isOidcUser
                    ? ProfileClaim(user, oidcOptions.EmailClaim, ClaimTypes.Email, "email")
                    : user.FindFirstValue(ClaimTypes.Email))
                : null,
            authenticated && (user.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.Administrator)
                || user.HasClaim(StructaDocClaimTypes.Administrator, bool.TrueString)),
            oidcOptions.Enabled,
            setupRequired);
    }

    // Where the display name and email of an identity-provider session come from. The OIDC handler
    // keeps inbound claims under their raw names (MapInboundClaims is off), so a deployment whose
    // authority writes them to a non-standard claim — `Oidc:NameClaim`/`Oidc:EmailClaim` — is read
    // here from the merged principal, which is also where the UserInfo endpoint's claims have
    // already landed. The standard names stay as fallbacks, so a default mapping resolves exactly
    // as it did before the mapping was configurable.
    private static string? ProfileClaim(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirstValue(claimType);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
