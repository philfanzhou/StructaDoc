using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Resources;
using StructaDoc.Contracts.Resources;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Resources;

public static class ResourceDeletionEndpoints
{
    public static IEndpointRouteBuilder MapResourceDeletionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/api/v1/documents/{id:guid}", DeleteDocumentAsync).WithName("DeleteDocument").RequireAuthorization(AuthorizationPolicies.DocumentsWrite).RequiresDocumentPermission(DocumentPermissions.Delete).Produces<ResourceDeletionResponse>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        endpoints.MapDelete("/api/v1/parse-runs/{id:guid}", DeleteParseRunAsync).WithName("DeleteParseRun").RequireAuthorization(AuthorizationPolicies.ParsesWrite).RequiresDocumentPermission(DocumentPermissions.Delete).Produces<ResourceDeletionResponse>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    private static async Task<IResult> DeleteDocumentAsync(Guid id, HttpContext context, IAntiforgery antiforgery, IResourceDeletionService service, TimeProvider clock, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(context, antiforgery); if (invalid is not null) return invalid;
        return ToResult(await service.RequestDocumentDeletionAsync(id, ResourceAccessContextFactory.Create(context.User), clock.GetUtcNow().UtcDateTime, cancellationToken));
    }

    private static async Task<IResult> DeleteParseRunAsync(Guid id, HttpContext context, IAntiforgery antiforgery, IResourceDeletionService service, TimeProvider clock, CancellationToken cancellationToken)
    {
        var invalid = await ValidateAsync(context, antiforgery); if (invalid is not null) return invalid;
        return ToResult(await service.RequestParseRunDeletionAsync(id, ResourceAccessContextFactory.Create(context.User), clock.GetUtcNow().UtcDateTime, cancellationToken));
    }

    private static IResult ToResult(ResourceDeletionResult result) => result.Status switch
    {
        ResourceDeletionStatus.Accepted => Accepted(result),
        ResourceDeletionStatus.AlreadyPending => Accepted(result),
        ResourceDeletionStatus.ActiveParseRuns => Results.Problem(statusCode: 409, title: "Resource is active", detail: "Deletion is allowed only after all affected Parse Runs reach a final state."),
        _ => Results.Problem(statusCode: 404, title: "Resource not found", detail: "The resource does not exist or is not accessible."),
    };

    // Named rather than anonymous so the shape can be described: an anonymous type is absent
    // from the API description, which leaves the caller the job of guessing what to poll.
    private static IResult Accepted(ResourceDeletionResult result) => Results.Accepted(value: new ResourceDeletionResponse(result.CleanupJobId, "deletion-pending"));

    private static async Task<IResult?> ValidateAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.User.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.ApiClient)) return null;
        try { await antiforgery.ValidateRequestAsync(context); return null; }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Antiforgery validation failed"); }
    }
}
