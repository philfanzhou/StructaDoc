using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Settings;

public static class OidcEndpoints
{
    public static IEndpointRouteBuilder MapOidcSettings(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/settings/oidc")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .RequireServiceMantleSecurityResponseHeaders();

        group.MapGet("", GetStatus).Produces<OidcStatusResponse>();
        group.MapPost("/test", TestAsync)
            .RequireRateLimiting(ConnectionTestRateLimiting.PolicyName)
            .Produces<OidcConnectionTestResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static IResult GetStatus(
        OidcOptions options,
        StartupFault fault)
    {
        return Results.Ok(new OidcStatusResponse(
            options.Enabled,
            fault.DetailFor(SettingCatalog.OidcSection),
            options.CallbackPath,
            options.SignedOutCallbackPath,
            options.EffectiveScopes));
    }

    private static async Task<IResult> TestAsync(
        OidcConnectionTestRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        OidcDiscoveryProbe probe,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await probe.ProbeAsync(
            request.Authority,
            request.RequireHttpsMetadata,
            cancellationToken);

        return Results.Ok(new OidcConnectionTestResponse(
            result.Succeeded,
            result.Code,
            result.Detail,
            result.Issuer));
    }
}
