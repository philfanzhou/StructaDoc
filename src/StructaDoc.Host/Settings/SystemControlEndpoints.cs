using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Settings;

public static class SystemControlEndpoints
{
    public static IEndpointRouteBuilder MapSystemControlEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/system")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/restart", RestartAsync)
            .Produces<RestartAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;

        async Task<IResult> RestartAsync(
            HttpContext context,
            IAntiforgery antiforgery,
            IHostApplicationLifetime lifetime,
            ILoggerFactory loggerFactory)
        {
            var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
            if (antiforgeryFailure is not null)
            {
                return antiforgeryFailure;
            }

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
