using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Settings;
using StructaDoc.Contracts.Settings;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Settings;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/settings")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("", ListAsync)
            .Produces<IReadOnlyList<SettingResponse>>();
        group.MapPut("", UpdateAsync)
            .Produces<SettingUpdateResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ISettingsService settings,
        CancellationToken cancellationToken)
    {
        var states = await settings.ListAsync(cancellationToken);
        return Results.Ok(states.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> UpdateAsync(
        SettingUpdateRequest request,
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        ISettingsService settings,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await settings.SetAsync(
            request.Key,
            request.Value,
            user.FindFirstValue(StructaDocClaimTypes.Username) ?? "unknown",
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        return result.Status switch
        {
            SettingWriteStatus.Succeeded => Results.Ok(
                new SettingUpdateResponse(result.RestartRequired)),

            SettingWriteStatus.UnknownKey => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown setting",
                detail: $"'{request.Key}' is not a setting this service publishes."),

            SettingWriteStatus.ManagedExternally => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Setting is managed by the deployment",
                detail: "This value comes from an environment variable or the command line, which takes precedence. Remove it there to manage the setting here."),

            _ => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid value",
                detail: $"'{request.Value}' is not a value '{request.Key}' accepts."),
        };
    }

    private static SettingResponse ToResponse(SettingState state)
    {
        return new SettingResponse(
            state.Key,
            state.Kind.ToString(),
            state.Value,
            state.RequiresRestart,
            state.IsManagedExternally,
            state.IsStored,
            state.IsPendingRestart,
            state.Minimum,
            state.Maximum,
            state.AllowedValues);
    }
}
