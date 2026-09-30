using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Auditing;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Settings;

public static class SystemControlEndpoints
{
    public static IEndpointRouteBuilder MapSystemControlEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/system")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .RequireServiceMantleSecurityResponseHeaders();

        group.MapPost("/restart", RestartAsync)
            .Produces<RestartAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;

        async Task<IResult> RestartAsync(
            ClaimsPrincipal user,
            HttpContext context,
            IAntiforgery antiforgery,
            IHostApplicationLifetime lifetime,
            StructaDocManagementAuditRecorder auditRecorder,
            ILoggerFactory loggerFactory)
        {
            var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
            if (antiforgeryFailure is not null)
            {
                return antiforgeryFailure;
            }

            // The audit record is written before the stop is scheduled: the process is about to
            // disappear, and an audit that races the shutdown is an audit that can be lost.
            var auditEvent = ManagementAuditEvent.Create(
                HostManagementAudit.AdministratorOperator(user),
                StructaDocManagementAudit.Actions.RestartRequested,
                ManagementAuditTarget.Create(
                    WellKnownManagementAuditTargetTypes.Service,
                    "structadoc"),
                ManagementAuditOutcome.Success,
                clientIp: HostManagementAudit.ClientAddress(context),
                securityDescription: "An administrator requested a service restart.",
                timeProvider: TimeProvider.System);
            await HostManagementAudit.RecordAsync(
                auditRecorder,
                loggerFactory.CreateLogger<ManagementAuditRecordPoints>(),
                auditEvent,
                "system restart request",
                CancellationToken.None);

            loggerFactory
                .CreateLogger("StructaDoc.SystemControl")
                .LogWarning("Administrator requested a restart. Stopping the Host.");

            // The Host can only stop itself. What brings it back is the container restart policy, so
            // a deployment started without one stays down until it is started again by hand. The
            // stop is scheduled after the response so the caller learns the request was accepted.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                lifetime.StopApplication();
            });

            return Results.Accepted(
                value: new RestartAcceptedResponse(
                    "The service is stopping. It comes back only if the container was started with a restart policy such as --restart unless-stopped."));
        }
    }
}
