using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Authentication;
using StructaDoc.Host.Auditing;

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
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IApiClientAdministrationService service,
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

        if (!TryCreateDefinition(request, out var definition, out var validationFailure))
        {
            return validationFailure;
        }

        var issuedClient = await service.CreateAsync(
            definition!,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        // The issued credential itself never reaches the audit trail: the target identity and the
        // client's name are the whole record.
        await RecordClientAuditAsync(
            auditRecorder,
            logger,
            user,
            StructaDocManagementAudit.Actions.ApiClientCreated,
            issuedClient.Client.Id.ToString("D"),
            issuedClient.Client.Name,
            ManagementAuditOutcome.Success,
            "An API client was created.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
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
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IApiClientAdministrationService service,
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

        var status = await service.RevokeAsync(
            id,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        // A revoke of an already-revoked client still records: the state it produced is the state
        // that was requested, and the actor asking for it twice is part of the trail.
        await RecordClientAuditAsync(
            auditRecorder,
            logger,
            user,
            StructaDocManagementAudit.Actions.ApiClientRevoked,
            id.ToString("D"),
            id.ToString("D"),
            status == ApiClientMutationStatus.NotFound
                ? ManagementAuditOutcome.Failure
                : ManagementAuditOutcome.Success,
            status == ApiClientMutationStatus.NotFound
                ? "The revocation named an API client that does not exist."
                : "An API client was revoked.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
            cancellationToken);

        return status == ApiClientMutationStatus.NotFound
            ? NotFound(id)
            : Results.NoContent();
    }

    /// <summary>
    /// The API client record point. The client's name is the only metadata; the issued credential
    /// never enters an audit field.
    /// </summary>
    private static async Task RecordClientAuditAsync(
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger logger,
        ClaimsPrincipal user,
        ManagementAuditAction action,
        string clientId,
        string clientName,
        ManagementAuditOutcome outcome,
        string? securityDescription,
        TimeProvider timeProvider,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        var auditEvent = ManagementAuditEvent.Create(
            HostManagementAudit.AdministratorOperator(user),
            action,
            ManagementAuditTarget.Create(
                StructaDocManagementAudit.TargetTypes.ApiClient,
                clientId),
            outcome,
            clientIp: clientIp,
            securityDescription: securityDescription,
            metadata: new Dictionary<string, string>
            {
                ["name"] = clientName,
            },
            timeProvider: timeProvider);

        await HostManagementAudit.RecordAsync(
            auditRecorder,
            logger,
            auditEvent,
            "API client administration",
            cancellationToken);
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
