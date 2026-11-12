using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using ServiceMantle.Web.RateLimiting;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Host.Auditing;

namespace StructaDoc.Host.Authentication;

public static class AdministratorSessionEndpoints
{
    public static IEndpointRouteBuilder MapAdministratorSessionEndpoints(
        this IEndpointRouteBuilder endpoints,
        TimeSpan sessionLifetime,
        OidcAuthenticationOptions oidcOptions)
    {
        var group = endpoints.MapGroup("/api/v1/admin")
            .RequireServiceMantleSecurityResponseHeaders();

        group.MapGet("/antiforgery", GetAntiforgeryToken)
            .AllowAnonymous()
            .Produces<AntiforgeryTokenResponse>();
        // The identity-provider sign-in entry of the administration area. Unlike the password
        // sign-in below it carries no credential to guess, so it shares none of the sign-in rate
        // limiting; the pre-sign-in administrator gate on the callback is what bounds it. A
        // redirect is the whole answer: the browser leaves for the identity provider and comes
        // back either an administrator or on the fixed not-admin landing — never signed in as a
        // plain workspace session it did not ask for.
        group.MapGet("/session/login", (HttpContext context, string? returnUrl) =>
                OidcSignInFlow.BeginSignInAsync(context, oidcOptions, returnUrl, administratorEntry: true, "/admin"))
            .AllowAnonymous()
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        // The sign-in limiter (`servicemantle.management`) is isolated from the setup one, so a
        // burst of first-run claims cannot lock out sign-in or vice versa. The endpoint is
        // anonymous, so the partition is the caller's address; a StructaDoc principal carries no
        // ServiceMantle management identity, so even a signed-in caller stays in the same
        // address-partitioned bucket.
        group.MapPost("/session", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingDefaults.ManagementPolicyName)
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
            StructaDocManagementAuditRecorder auditRecorder,
            ILogger<ManagementAuditRecordPoints> logger,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
            if (antiforgeryFailure is not null)
            {
                return antiforgeryFailure;
            }

            var administrator = await authenticationService.AuthenticateAsync(
                request.Username,
                request.Password,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);

            if (administrator is null)
            {
                await RecordLoginAuditAsync(
                    auditRecorder,
                    logger,
                    // A failed sign-in has no established identity, so the event is attributed to
                    // the username that was submitted.
                    StructaDocManagementAudit.LoginAttemptOperator(request.Username),
                    succeeded: false,
                    request.Username,
                    timeProvider,
                    HostManagementAudit.ClientAddress(context),
                    cancellationToken);
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Authentication failed",
                    detail: "The username or password is invalid.");
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

            await RecordLoginAuditAsync(
                auditRecorder,
                logger,
                StructaDocManagementAudit.AdministratorOperator(
                    administrator.Id.ToString("D"),
                    administrator.Username),
                succeeded: true,
                administrator.Id.ToString("D"),
                timeProvider,
                HostManagementAudit.ClientAddress(context),
                cancellationToken);

            return Results.Ok(ToResponse(administrator));
        }
    }

    private static async Task RecordLoginAuditAsync(
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger logger,
        ManagementAuditOperator operatorInfo,
        bool succeeded,
        string targetId,
        TimeProvider timeProvider,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        var auditEvent = ManagementAuditEvent.Create(
            operatorInfo,
            succeeded
                ? WellKnownManagementAuditActions.AdminLoginSucceeded
                : WellKnownManagementAuditActions.AdminLoginFailed,
            ManagementAuditTarget.Create(
                WellKnownManagementAuditTargetTypes.AdminSession,
                targetId),
            succeeded ? ManagementAuditOutcome.Success : ManagementAuditOutcome.Failure,
            clientIp: clientIp,
            timeProvider: timeProvider);

        await HostManagementAudit.RecordAsync(
            auditRecorder,
            logger,
            auditEvent,
            "administrator login",
            cancellationToken);
    }

    private static IResult GetCurrentSession(ClaimsPrincipal user)
    {
        // The endpoint reports the local administrator account behind the session. A subject that
        // signed in through the identity provider may hold the administrator policy without
        // having a local control-plane account to report, so it is answered with denial rather
        // than a parse failure on a subject identifier that was never a local account id.
        if (!user.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.Administrator))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The session is not a local administrator session");
        }

        return Results.Ok(new AdministratorSessionResponse(
            Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            user.FindFirstValue(StructaDocClaimTypes.Username)!,
            user.FindFirstValue(ClaimTypes.Name)!));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        await context.SignOutAsync(AuthenticationSchemes.AdministratorCookie);
        return Results.NoContent();
    }

    internal static ClaimsPrincipal CreatePrincipal(AuthenticatedAdministrator administrator)
    {
        var claims = new Claim[]
        {
            new(ClaimTypes.NameIdentifier, administrator.Id.ToString("D")),
            new(StructaDocClaimTypes.Username, administrator.Username),
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
            administrator.Username,
            administrator.DisplayName);
    }
}
