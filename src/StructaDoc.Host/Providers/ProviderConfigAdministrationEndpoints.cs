using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Providers;
using StructaDoc.Contracts.Providers;
using StructaDoc.Host.Auditing;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Providers;

public static class ProviderConfigAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapProviderConfigAdministrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        // What each type needs and what it defaults to, so the form can say so instead of leaving an
        // administrator to guess an address and wonder what "leave blank" produces. It is served
        // rather than compiled into the browser bundle because the defaults belong to the adapters.
        endpoints.MapGet("/api/v1/admin/provider-types", ListTypes)
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .Produces<IReadOnlyList<ProviderTypeResponse>>();

        var group = endpoints.MapGroup("/api/v1/admin/provider-configs")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .RequireServiceMantleSecurityResponseHeaders();

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
        group.MapDelete("/{id:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static IResult ListTypes() => Results.Ok(
        ProviderTypeDescriptors.All.Select(ToResponse).ToArray());

    private static async Task<IResult> ListAsync(
        IProviderConfigAdministrationService service,
        CancellationToken cancellationToken)
    {
        var configs = await service.ListAsync(cancellationToken);
        return Results.Ok(configs.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        ProviderConfigRequest request,
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IProviderConfigAdministrationService service,
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger<ManagementAuditRecordPoints> logger,
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

        // Provider audits carry only the configuration identity and its version: the credential and
        // the endpoint address are configuration values, not identity.
        await RecordProviderAuditAsync(
            auditRecorder,
            logger,
            user,
            StructaDocManagementAudit.Actions.ProviderConfigCreated,
            result.Config?.Id.ToString("D") ?? Guid.Empty.ToString("D"),
            result.Config,
            outcome: result.Status == ProviderConfigMutationStatus.Succeeded
                ? ManagementAuditOutcome.Success
                : ManagementAuditOutcome.Failure,
            securityDescription: result.Status == ProviderConfigMutationStatus.Succeeded
                ? "A Provider configuration was created."
                : "The Provider configuration creation was rejected.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
            cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return result.Status == ProviderConfigMutationStatus.Succeeded
            ? Results.Json(ToResponse(result.Config!), statusCode: StatusCodes.Status201Created)
            : Conflict();
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        ProviderConfigRequest request,
        ClaimsPrincipal user,
        HttpContext context,
        IAntiforgery antiforgery,
        IProviderConfigAdministrationService service,
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger<ManagementAuditRecordPoints> logger,
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

        // The previous state decides which change this is: a pure enable or disable is its own
        // action, and anything else is a new configuration version.
        var existing = (await service.ListAsync(cancellationToken))
            .FirstOrDefault(config => config.Id == id);

        var result = await service.UpdateAsync(
            id,
            failure.Definition!,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        var action = (existing is null, request.IsEnabled) switch
        {
            (false, true) when !existing!.IsEnabled =>
                StructaDocManagementAudit.Actions.ProviderConfigEnabled,
            (false, false) when existing!.IsEnabled =>
                StructaDocManagementAudit.Actions.ProviderConfigDisabled,
            _ => StructaDocManagementAudit.Actions.ProviderConfigChanged,
        };

        await RecordProviderAuditAsync(
            auditRecorder,
            logger,
            user,
            action,
            id.ToString("D"),
            result.Config,
            outcome: result.Status == ProviderConfigMutationStatus.Succeeded
                ? ManagementAuditOutcome.Success
                : ManagementAuditOutcome.Failure,
            securityDescription: result.Status == ProviderConfigMutationStatus.Succeeded
                ? "A Provider configuration changed: a new immutable version now applies."
                : "The Provider configuration update was rejected.",
            timeProvider,
            HostManagementAudit.ClientAddress(context),
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

    private static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext context,
        IAntiforgery antiforgery,
        IProviderConfigAdministrationService service,
        CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await AntiforgeryGuard.ValidateAsync(context, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var status = await service.DeleteAsync(id, cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return status switch
        {
            ProviderConfigDeletionStatus.Deleted => Results.NoContent(),

            ProviderConfigDeletionStatus.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Provider Config not found",
                detail: $"Provider Config '{id:D}' does not exist."),

            ProviderConfigDeletionStatus.ReferencedByActiveParseRun => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Provider Config is in use",
                detail: "A Parse Run that has not finished still uses this Provider Config. Wait for it to finish or cancel it, then delete the Provider Config."),

            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Provider Config has parse history",
                detail: "Finished Parse Runs record the configuration version they were produced with, so this Provider Config cannot be removed. Disable it instead to stop new Parse Runs from using it."),
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

    private static ProviderTypeResponse ToResponse(ProviderTypeDescriptor descriptor) => new(
        descriptor.ProviderType,
        descriptor.SuggestedBaseUrl,
        descriptor.RequiresCredential,
        new ProviderSettingResponse(descriptor.Model.IsUsed, descriptor.Model.AppliedDefault),
        new ProviderSettingResponse(descriptor.Backend.IsUsed, descriptor.Backend.AppliedDefault));

    /// <summary>
    /// The Provider configuration record point. The metadata allowlist is the configuration's
    /// identity and version only — name, type, version number, and the resulting enabled and
    /// default state — never the credential or the endpoint address.
    /// </summary>
    private static async Task RecordProviderAuditAsync(
        StructaDocManagementAuditRecorder auditRecorder,
        ILogger logger,
        ClaimsPrincipal user,
        ManagementAuditAction action,
        string providerConfigId,
        ProviderConfigRecord? config,
        ManagementAuditOutcome outcome,
        string? securityDescription,
        TimeProvider timeProvider,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>();
        if (config is not null)
        {
            metadata["name"] = config.Name;
            metadata["providerType"] = config.ProviderType;
            metadata["version"] = config.VersionNumber.ToString(CultureInfo.InvariantCulture);
            metadata["isEnabled"] = config.IsEnabled.ToString(CultureInfo.InvariantCulture);
            metadata["isDefault"] = config.IsDefault.ToString(CultureInfo.InvariantCulture);
        }

        var auditEvent = ManagementAuditEvent.Create(
            HostManagementAudit.AdministratorOperator(user),
            action,
            ManagementAuditTarget.Create(
                StructaDocManagementAudit.TargetTypes.ProviderConfig,
                providerConfigId),
            outcome,
            clientIp: clientIp,
            securityDescription: securityDescription,
            metadata: metadata,
            timeProvider: timeProvider);

        await HostManagementAudit.RecordAsync(
            auditRecorder,
            logger,
            auditEvent,
            "Provider configuration administration",
            cancellationToken);
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
