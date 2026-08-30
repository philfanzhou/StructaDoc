using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Adapters.Authentication;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Settings;

public static class OidcSettingsEndpoints
{
    public static IEndpointRouteBuilder MapOidcSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/settings/oidc")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("", GetStatus).Produces<OidcStatusResponse>();
        group.MapPost("/test", TestAsync)
            .Produces<OidcConnectionTestResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static IResult GetStatus(
        OidcAuthenticationOptions options,
        SettingsStartupFault fault)
    {
        return Results.Ok(new OidcStatusResponse(
            options.Enabled,
            fault.DetailFor(SettingCatalog.OidcSection),
            options.CallbackPath,
            options.SignedOutCallbackPath,
            options.Scopes));
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
