using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Setup;
using StructaDoc.Host.Auditing;
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

            var clientAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var peerAddress = context.Connection.RemoteIpAddress?.ToString();
            var result = await provisioning.ClaimFirstAdministratorAsync(
                request.Username,
                request.Password,
                request.DisplayName,
                clientAddress,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);

            if (result.Outcome != AdministratorClaimOutcome.Created)
            {
                await RecordClaimAuditAsync(
                    auditRecorder,
                    logger,
                    request.Username,
                    result.Administrator?.Id.ToString("D"),
                    outcome: result.Outcome switch
                    {
                        AdministratorClaimOutcome.AlreadyClaimed => ManagementAuditOutcome.Denied,
                        _ => ManagementAuditOutcome.Failure,
                    },
                    peerAddress,
                    timeProvider,
                    cancellationToken);
            }

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
            await RecordClaimAuditAsync(
                auditRecorder,
                logger,
                administrator.Username,
                administrator.Id.ToString("D"),
                ManagementAuditOutcome.Success,
                peerAddress,
                timeProvider,
                cancellationToken);

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

        // The claim creates the first administrator, which is the one management action that
        // happens without an authenticated operator: the account the claim produced is the only
        // identity the event can carry, and the address it came from is part of the record.
        static async Task RecordClaimAuditAsync(
            StructaDocManagementAuditRecorder auditRecorder,
            ILogger logger,
            string submittedUsername,
            string? claimedAdministratorId,
            ManagementAuditOutcome outcome,
            string? clientIp,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            var auditEvent = ManagementAuditEvent.Create(
                claimedAdministratorId is null
                    ? StructaDocManagementAudit.LoginAttemptOperator(submittedUsername)
                    : StructaDocManagementAudit.AdministratorOperator(
                        claimedAdministratorId,
                        submittedUsername),
                StructaDocManagementAudit.Actions.SetupClaimed,
                ManagementAuditTarget.Create(
                    StructaDocManagementAudit.TargetTypes.AdministratorAccount,
                    claimedAdministratorId ?? submittedUsername),
                outcome,
                // The audit client IP is a structured field that must be a real address; a
                // request whose peer address is unknown records none.
                clientIp: clientIp,
                securityDescription: outcome == ManagementAuditOutcome.Success
                    ? "The first administrator account was created through setup."
                    : "An attempt to claim setup was rejected.",
                timeProvider: timeProvider);

            await HostManagementAudit.RecordAsync(
                auditRecorder,
                logger,
                auditEvent,
                "setup claim",
                cancellationToken);
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
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
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
}
