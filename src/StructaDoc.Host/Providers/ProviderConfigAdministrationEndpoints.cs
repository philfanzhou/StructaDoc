using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Providers;
using StructaDoc.Contracts.Providers;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Providers;

public static class ProviderConfigAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapProviderConfigAdministrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/provider-configs")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("", ListAsync).Produces<IReadOnlyList<ProviderConfigResponse>>();
        group.MapPost("", CreateAsync)
            .Produces<ProviderConfigResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id:guid}", UpdateAsync)
            .Produces<ProviderConfigResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IProviderConfigAdministrationService service,
        CancellationToken cancellationToken)
    {
        var configs = await service.ListAsync(cancellationToken);
        return Results.Ok(configs.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        ProviderConfigRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        IProviderConfigAdministrationService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var failure = await ValidateRequestAsync(
            request,
            context,
            antiforgery,
            allowClearCredential: false);
        if (failure.Result is not null)
        {
            return failure.Result;
        }

        var result = await service.CreateAsync(
            failure.Definition!,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return result.Status == ProviderConfigMutationStatus.Succeeded
            ? Results.Json(ToResponse(result.Config!), statusCode: StatusCodes.Status201Created)
            : Conflict();
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        ProviderConfigRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        IProviderConfigAdministrationService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var failure = await ValidateRequestAsync(
            request,
            context,
            antiforgery,
            allowClearCredential: true);
        if (failure.Result is not null)
        {
            return failure.Result;
        }

        var result = await service.UpdateAsync(
            id,
            failure.Definition!,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return result.Status switch
        {
            ProviderConfigMutationStatus.Succeeded => Results.Ok(ToResponse(result.Config!)),
            ProviderConfigMutationStatus.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Provider Config not found",
                detail: $"Provider Config '{id:D}' does not exist."),
            _ => Conflict(),
        };
    }

    private static async Task<(ProviderConfigDefinition? Definition, IResult? Result)> ValidateRequestAsync(
        ProviderConfigRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        bool allowClearCredential)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return (null, Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Antiforgery validation failed",
                detail: "A valid antiforgery token is required."));
        }

        if (!allowClearCredential && request.ClearCredential)
        {
            return (null, Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["clearCredential"] = ["clearCredential is only valid when updating a Provider Config."],
                }));
        }

        if (ProviderConfigDefinition.TryCreate(
                request.Name,
                request.ProviderType,
                request.BaseUrl,
                request.Model,
                request.Backend,
                request.Credential,
                request.ClearCredential,
                request.IsEnabled,
                request.IsDefault,
                out var definition,
                out var field,
                out var message))
        {
            return (definition, null);
        }

        return (null, Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }));
    }

    private static ProviderConfigResponse ToResponse(ProviderConfigRecord config) => new(
        config.Id,
        config.Name,
        config.ProviderType,
        config.IsEnabled,
        config.IsDefault,
        config.CurrentVersionId,
        config.VersionNumber,
        config.BaseUrl,
        config.Model,
        config.Backend,
        config.HasCredential,
        config.CreatedAtUtc,
        config.UpdatedAtUtc);

    private static IResult Conflict() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Provider Config cannot be changed",
        detail: "The Provider Config conflicts with current state or was changed concurrently.");
}
