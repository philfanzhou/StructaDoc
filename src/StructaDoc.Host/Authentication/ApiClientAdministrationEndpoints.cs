using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;

namespace StructaDoc.Host.Authentication;

public static class ApiClientAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapApiClientAdministrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/api-clients")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("", ListAsync)
            .Produces<IReadOnlyList<ApiClientResponse>>();
        group.MapPost("", CreateAsync)
            .Produces<ApiClientCredentialResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapPut("/{id:guid}", UpdateAsync)
            .Produces<ApiClientResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{id:guid}/rotate", RotateAsync)
            .Produces<ApiClientCredentialResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{id:guid}", RevokeAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IApiClientAdministrationService service,
        CancellationToken cancellationToken)
    {
        var clients = await service.ListAsync(cancellationToken);
        return Results.Ok(clients.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        ApiClientRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        IApiClientAdministrationService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);

        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        if (!TryCreateDefinition(request, out var definition, out var validationFailure))
        {
            return validationFailure;
        }

        var issuedClient = await service.CreateAsync(
            definition!,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            ToCredentialResponse(issuedClient),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        ApiClientRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        IApiClientAdministrationService service,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);

        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        if (!TryCreateDefinition(request, out var definition, out var validationFailure))
        {
            return validationFailure;
        }

        var result = await service.UpdateAsync(id, definition!, cancellationToken);
        return result.Status switch
        {
            ApiClientMutationStatus.Succeeded => Results.Ok(ToResponse(result.Client!)),
            ApiClientMutationStatus.NotFound => NotFound(id),
            _ => Conflict(id),
        };
    }

    private static async Task<IResult> RotateAsync(
        Guid id,
        HttpContext context,
        IAntiforgery antiforgery,
        IApiClientAdministrationService service,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);

        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var result = await service.RotateCredentialAsync(id, cancellationToken);

        if (result.Status == ApiClientMutationStatus.Succeeded)
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        return result.Status switch
        {
            ApiClientMutationStatus.Succeeded => Results.Ok(
                ToCredentialResponse(result.IssuedClient!)),
            ApiClientMutationStatus.NotFound => NotFound(id),
            _ => Conflict(id),
        };
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        HttpContext context,
        IAntiforgery antiforgery,
        IApiClientAdministrationService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);

        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var status = await service.RevokeAsync(
            id,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        return status == ApiClientMutationStatus.NotFound
            ? NotFound(id)
            : Results.NoContent();
    }

    private static bool TryCreateDefinition(
        ApiClientRequest request,
        out ApiClientDefinition? definition,
        out IResult validationFailure)
    {
        if (ApiClientDefinition.TryCreate(
                request.Name,
                request.Scopes,
                out definition,
                out var errorField,
                out var errorMessage))
        {
            validationFailure = Results.Empty;
            return true;
        }

        validationFailure = Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                [errorField] = [errorMessage],
            });
        return false;
    }

    private static ApiClientResponse ToResponse(ApiClientRecord client)
    {
        return new ApiClientResponse(
            client.Id,
            client.Name,
            client.Scopes,
            client.IsActive,
            client.CreatedAtUtc,
            client.RevokedAtUtc);
    }

    private static ApiClientCredentialResponse ToCredentialResponse(IssuedApiClient client)
    {
        return new ApiClientCredentialResponse(
            ToResponse(client.Client),
            client.Credential);
    }

    private static IResult NotFound(Guid id)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "API Client not found",
            detail: $"API Client '{id:D}' does not exist.");
    }

    private static IResult Conflict(Guid id)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "API Client cannot be changed",
            detail: $"API Client '{id:D}' is revoked or was changed concurrently.");
    }
}
