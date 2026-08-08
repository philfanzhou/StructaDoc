using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Setup;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Setup;

public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(
        this IEndpointRouteBuilder endpoints,
        TimeSpan sessionLifetime)
    {
        var group = endpoints.MapGroup("/api/v1/setup");

        group.MapGet("", GetStatusAsync)
            .AllowAnonymous()
            .Produces<SetupStatusResponse>();

        // Anonymous by necessity: first run has no account to authenticate against. The same rate
        // limit as administrator sign-in applies, because this endpoint also mints an administrator.
        group.MapPost("", ClaimAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthorizationPolicies.AdministratorLoginRateLimit)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var claimGroup = endpoints.MapGroup("/api/v1/admin/setup-claim")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        claimGroup.MapGet("", GetClaimWarningAsync)
            .Produces<SetupClaimWarningResponse>()
            .Produces(StatusCodes.Status204NoContent);

        claimGroup.MapPost("/acknowledge", AcknowledgeClaimAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;

        async Task<IResult> ClaimAsync(
            SetupClaimRequest request,
            HttpContext context,
            IAntiforgery antiforgery,
            IAdministratorProvisioningService provisioning,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            if (!await ValidateAntiforgeryAsync(context, antiforgery))
            {
                return AntiforgeryProblem();
            }

            var result = await provisioning.ClaimFirstAdministratorAsync(
                request.Username,
                request.Password,
                request.DisplayName,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);

            switch (result.Outcome)
            {
                case AdministratorClaimOutcome.InvalidUsername:
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid username",
                        detail: $"A username contains {AdministratorUsernamePolicy.MinimumLength} to {AdministratorUsernamePolicy.MaximumLength} letters, digits, '.', '_', or '-', and starts and ends with a letter or digit.");

                case AdministratorClaimOutcome.InvalidPassword:
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid password",
                        detail: $"A password contains {AdministratorPasswordPolicy.MinimumLength} to {AdministratorPasswordPolicy.MaximumLength} characters.");

                case AdministratorClaimOutcome.AlreadyClaimed:
                    // Setup does not exist once an administrator does, so a late caller is told the
                    // same thing an unrelated visitor would be told.
                    return SetupCompletedProblem();
            }

            var administrator = result.Administrator!;
            await context.SignInAsync(
                AuthenticationSchemes.AdministratorCookie,
                AdministratorSessionEndpoints.CreatePrincipal(administrator),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = timeProvider.GetUtcNow().Add(sessionLifetime),
                });

            return Results.NoContent();
        }
    }

    private static async Task<IResult> GetStatusAsync(
        IAdministratorProvisioningService provisioning,
        CancellationToken cancellationToken)
    {
        var exists = await provisioning.AnyAdministratorExistsAsync(cancellationToken);
        return Results.Ok(new SetupStatusResponse(!exists));
    }

    private static async Task<IResult> GetClaimWarningAsync(
        IAdministratorProvisioningService provisioning,
        CancellationToken cancellationToken)
    {
        var claim = await provisioning.GetUnacknowledgedClaimAsync(cancellationToken);
        return claim is null
            ? Results.NoContent()
            : Results.Ok(new SetupClaimWarningResponse(claim.ClaimedFromAddress, claim.ClaimedAtUtc));
    }

    private static async Task<IResult> AcknowledgeClaimAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IAdministratorProvisioningService provisioning,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
        {
            return AntiforgeryProblem();
        }

        await provisioning.AcknowledgeClaimAsync(
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        return Results.NoContent();
    }

    private static IResult SetupCompletedProblem()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Setup is not available",
            detail: "An administrator already exists.");
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
