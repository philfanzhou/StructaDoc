using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Resources;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Resources;

public static class ResourceDeletionEndpoints
{
    public static IEndpointRouteBuilder MapResourceDeletionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/api/v1/documents/{id:guid}", DeleteDocumentAsync).RequireAuthorization(AuthorizationPolicies.DocumentsWrite);
        endpoints.MapDelete("/api/v1/parse-runs/{id:guid}", DeleteParseRunAsync).RequireAuthorization(AuthorizationPolicies.ParsesWrite);
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
        ResourceDeletionStatus.Accepted => Results.Accepted(value: new { cleanupJobId = result.CleanupJobId, status = "deletion-pending" }),
        ResourceDeletionStatus.AlreadyPending => Results.Accepted(value: new { cleanupJobId = result.CleanupJobId, status = "deletion-pending" }),
        ResourceDeletionStatus.ActiveParseRuns => Results.Problem(statusCode: 409, title: "Resource is active", detail: "Deletion is allowed only after all affected Parse Runs reach a final state."),
        _ => Results.Problem(statusCode: 404, title: "Resource not found", detail: "The resource does not exist or is not accessible."),
    };

    private static async Task<IResult?> ValidateAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.User.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.ApiClient)) return null;
        try { await antiforgery.ValidateRequestAsync(context); return null; }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Antiforgery validation failed"); }
    }
}
