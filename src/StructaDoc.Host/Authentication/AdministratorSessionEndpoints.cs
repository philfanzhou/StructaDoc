using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Authentication;

public static class AdministratorSessionEndpoints
{
    public static IEndpointRouteBuilder MapAdministratorSessionEndpoints(
        this IEndpointRouteBuilder endpoints,
        TimeSpan sessionLifetime)
    {
        var group = endpoints.MapGroup("/api/v1/admin");

        group.MapGet("/antiforgery", GetAntiforgeryToken)
            .AllowAnonymous()
            .Produces<AntiforgeryTokenResponse>();
        group.MapPost("/session", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthorizationPolicies.AdministratorLoginRateLimit)
            .Produces<AdministratorSessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        group.MapGet("/session", GetCurrentSession)
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .Produces<AdministratorSessionResponse>();
        group.MapDelete("/session", LogoutAsync)
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;

        IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new AntiforgeryTokenResponse(
                tokens.RequestToken
                    ?? throw new InvalidOperationException("Antiforgery request token was not generated."),
                tokens.HeaderName ?? "X-CSRF-TOKEN"));
        }

        async Task<IResult> LoginAsync(
            AdministratorLoginRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            IAdministratorAuthenticationService authenticationService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            if (!await ValidateAntiforgeryAsync(context, antiforgery))
            {
                return AntiforgeryProblem();
            }

            var administrator = await authenticationService.AuthenticateAsync(
                request.Email,
                request.Password,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);

            if (administrator is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Authentication failed",
                    detail: "The email or password is invalid.");
            }

            var principal = CreatePrincipal(administrator);
            await context.SignInAsync(
                AuthenticationSchemes.AdministratorCookie,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = timeProvider.GetUtcNow().Add(sessionLifetime),
                });

            return Results.Ok(ToResponse(administrator));
        }
    }

    private static IResult GetCurrentSession(ClaimsPrincipal user)
    {
        return Results.Ok(new AdministratorSessionResponse(
            Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            user.FindFirstValue(ClaimTypes.Email)!,
            user.FindFirstValue(ClaimTypes.Name)!));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
        {
            return AntiforgeryProblem();
        }

        await context.SignOutAsync(AuthenticationSchemes.AdministratorCookie);
        return Results.NoContent();
    }

    private static ClaimsPrincipal CreatePrincipal(AuthenticatedAdministrator administrator)
    {
        var claims = new Claim[]
        {
            new(ClaimTypes.NameIdentifier, administrator.Id.ToString("D")),
            new(ClaimTypes.Email, administrator.Email),
            new(ClaimTypes.Name, administrator.DisplayName),
            new(StructaDocClaimTypes.SubjectType, SubjectTypes.Administrator),
            new(StructaDocClaimTypes.SecurityStamp, administrator.SecurityStamp.ToString("D")),
        };
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, AuthenticationSchemes.AdministratorCookie));
    }

    private static AdministratorSessionResponse ToResponse(
        AuthenticatedAdministrator administrator)
    {
        return new AdministratorSessionResponse(
            administrator.Id,
            administrator.Email,
            administrator.DisplayName);
    }

    private static async Task<bool> ValidateAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static IResult AntiforgeryProblem()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Antiforgery validation failed",
            detail: "A valid antiforgery token is required.");
    }
}
