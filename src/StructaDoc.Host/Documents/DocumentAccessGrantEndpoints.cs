using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Contracts.Documents;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Documents;

public static class DocumentAccessGrantEndpoints
{
    private static readonly IReadOnlyDictionary<string, DocumentPermissions> PermissionMap = new Dictionary<string, DocumentPermissions>(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = DocumentPermissions.Read,
        ["write"] = DocumentPermissions.Write,
        ["parse"] = DocumentPermissions.Parse,
        ["export"] = DocumentPermissions.Export,
        ["delete"] = DocumentPermissions.Delete,
        ["share"] = DocumentPermissions.Share,
    };

    public static IEndpointRouteBuilder MapDocumentAccessGrantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/documents/{documentId:guid}/access-grants").RequireAuthorization(AuthorizationPolicies.DocumentsWrite);
        group.MapGet("", ListAsync);
        group.MapPost("", SetAsync).ProducesValidationProblem();
        group.MapDelete("/{grantId:guid}", RevokeAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid documentId, HttpContext context, IDocumentAuthorizationService service, CancellationToken cancellationToken)
    {
        var access = ResourceAccessContextFactory.Create(context.User);
        if (!await service.HasPermissionAsync(documentId, access, DocumentPermissions.Share, cancellationToken)) return NotFound(documentId);
        var grants = await service.ListGrantsAsync(documentId, access, cancellationToken);
        return Results.Ok(grants.Select(ToResponse));
    }

    private static async Task<IResult> SetAsync(Guid documentId, DocumentAccessGrantRequest request, HttpContext context, IAntiforgery antiforgery, IDocumentAuthorizationService service, TimeProvider clock, CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await ValidateAntiforgeryAsync(context, antiforgery);
        if (antiforgeryFailure is not null) return antiforgeryFailure;
        if (!PrincipalIdentity.IsValid(request.Issuer, request.Subject))
        {
            return Validation("identity", $"Issuer must be an HTTP(S) OIDC issuer with an ASCII subject of at most 255 characters, or '{PrincipalIdentity.ApiClientIssuer}' with an API client ID as the subject.");
        }
        var permissions = DocumentPermissions.None;
        foreach (var name in request.Permissions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!PermissionMap.TryGetValue(name, out var permission)) return Validation("permissions", $"Unknown permission '{name}'.");
            permissions |= permission;
        }
        if (permissions == DocumentPermissions.None) return Validation("permissions", "At least one permission is required.");
        var grant = await service.SetGrantAsync(documentId, ResourceAccessContextFactory.Create(context.User), request.Issuer, request.Subject, permissions, ResourceAccessContextFactory.GetActorId(context.User), clock.GetUtcNow().UtcDateTime, cancellationToken);
        return grant is null ? NotFound(documentId) : Results.Ok(ToResponse(grant));
    }

    private static async Task<IResult> RevokeAsync(Guid documentId, Guid grantId, HttpContext context, IAntiforgery antiforgery, IDocumentAuthorizationService service, CancellationToken cancellationToken)
    {
        var antiforgeryFailure = await ValidateAntiforgeryAsync(context, antiforgery);
        if (antiforgeryFailure is not null) return antiforgeryFailure;
        return await service.RevokeGrantAsync(documentId, ResourceAccessContextFactory.Create(context.User), grantId, cancellationToken) ? Results.NoContent() : NotFound(documentId);
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.User.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.ApiClient)) return null;
        try { await antiforgery.ValidateRequestAsync(context); return null; }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Antiforgery validation failed"); }
    }

    private static DocumentAccessGrantResponse ToResponse(DocumentAccessGrant grant) => new(grant.Id, grant.DocumentId, grant.Issuer, grant.Subject, PermissionMap.Where(item => grant.Permissions.HasFlag(item.Value)).Select(item => item.Key).ToArray(), grant.CreatedBy, grant.CreatedAtUtc);
    private static IResult NotFound(Guid id) => Results.Problem(statusCode: 404, title: "Document not found", detail: $"Document '{id:D}' does not exist or is not accessible.");
    private static IResult Validation(string field, string error) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });
}
