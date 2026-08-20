using Microsoft.AspNetCore.Antiforgery;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Contracts.Documents;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Documents;

public static class DocumentAccessGrantEndpoints
{
    // The accepted vocabulary, and also how a stored grant is rendered back: a name appears in a
    // response only if it appears here. `write` was removed from both directions at once, so a
    // grant written while it was accepted reports the rest of what it carries and stays valid.
    private static readonly IReadOnlyDictionary<string, DocumentPermissions> PermissionMap = new Dictionary<string, DocumentPermissions>(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = DocumentPermissions.Read,
        ["parse"] = DocumentPermissions.Parse,
        ["export"] = DocumentPermissions.Export,
        ["delete"] = DocumentPermissions.Delete,
        ["share"] = DocumentPermissions.Share,
    };

    public static IEndpointRouteBuilder MapDocumentAccessGrantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/documents/{documentId:guid}/access-grants").RequireAuthorization(AuthorizationPolicies.DocumentsWrite).RequiresDocumentPermission(DocumentPermissions.Share);
        group.MapGet("", ListAsync).WithName("ListDocumentAccessGrants").Produces<IReadOnlyList<DocumentAccessGrantResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", SetAsync).WithName("SetDocumentAccessGrant").Produces<DocumentAccessGrantResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/{grantId:guid}", RevokeAsync).WithName("RevokeDocumentAccessGrant").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    /// <summary>Lists the explicit access grants on a Document.</summary>
    /// <remarks>
    /// Permission names use the <c>read</c>, <c>parse</c>, <c>export</c>, <c>delete</c>, and
    /// <c>share</c> vocabulary.
    /// </remarks>
    internal static async Task<IResult> ListAsync(Guid documentId, HttpContext context, IDocumentAuthorizationService service, CancellationToken cancellationToken)
    {
        var access = ResourceAccessContextFactory.Create(context.User);
        if (!await service.HasPermissionAsync(documentId, access, RequiredDocumentPermission.Of(context), cancellationToken)) return NotFound(documentId);
        var grants = await service.ListGrantsAsync(documentId, access, cancellationToken);
        return Results.Ok(grants.Select(ToResponse));
    }

    /// <summary>Creates or replaces a grant for one OIDC user or API client.</summary>
    /// <remarks>
    /// Permission names are <c>read</c>, <c>parse</c>, <c>export</c>, <c>delete</c>, and
    /// <c>share</c>. The service returns 400 for an invalid identity, an unknown permission, an
    /// empty permission set, or a failed browser antiforgery check.
    /// </remarks>
    internal static async Task<IResult> SetAsync(Guid documentId, DocumentAccessGrantRequest request, HttpContext context, IAntiforgery antiforgery, IDocumentAuthorizationService service, TimeProvider clock, CancellationToken cancellationToken)
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

    /// <summary>Revokes one explicit access grant.</summary>
    /// <remarks>A failed browser antiforgery check returns 400.</remarks>
    internal static async Task<IResult> RevokeAsync(Guid documentId, Guid grantId, HttpContext context, IAntiforgery antiforgery, IDocumentAuthorizationService service, CancellationToken cancellationToken)
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
