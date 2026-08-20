using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.OpenApi;

// How each endpoint is reached, which is the part of the contract a generated document cannot see.
// An endpoint's signature says what it takes and returns; the authorization policy on it, and the
// Document permission declared beside it, say who may call it, and that is the first thing an
// integrator needs and the last thing they can infer from a route.
internal sealed class ApiSecurityTransformer : IOpenApiOperationTransformer
{
    private const string BrowserOnly =
        "Requires a browser session. An API client credential is refused here, whatever its scopes.";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        // An endpoint inside an authorized group can opt back out, and reading the group's policy
        // for it would describe a credential requirement that is not enforced.
        var policy = metadata.OfType<IAllowAnonymous>().Any()
            ? null
            : metadata
                .OfType<IAuthorizeData>()
                .Select(data => data.Policy)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        var note = policy switch
        {
            AuthorizationPolicies.DocumentsRead
                or AuthorizationPolicies.DocumentsWrite
                or AuthorizationPolicies.ParsesRead
                or AuthorizationPolicies.ParsesWrite => RequireScope(operation, context.Document, policy),
            AuthorizationPolicies.Administrator =>
                $"{BrowserOnly} The signed-in account must be an administrator.",
            AuthorizationPolicies.InteractiveUser => BrowserOnly,
            _ => "Open to unauthenticated callers.",
        };

        // The scope policy says which credential opens the door. On a Document-scoped route it
        // is only half the rule: the rest is a permission on that Document, checked against the
        // grants its owner handed out, and refused with `404` rather than `403` so that a caller
        // cannot learn a Document exists by being turned away from it.
        var permission = metadata.OfType<RequiredDocumentPermission>().FirstOrDefault();
        if (permission is not null)
        {
            note += $" The caller must also hold the `{permission.Permission}` permission on the "
                + "Document behind this resource, as its owner or through an access grant. Without "
                + "it the answer is `404`, the same answer as for a Document that does not exist.";
        }

        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? note
            : $"{operation.Description}\n\n{note}";

        return Task.CompletedTask;
    }

    private static string RequireScope(OpenApiOperation operation, OpenApiDocument? document, string scope)
    {
        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(ApiDocumentTransformer.ApiKeySchemeName, document)] = [],
            },
        ];

        // The scope is stated rather than carried in the security requirement because OpenAPI
        // attaches scopes to OAuth flows alone, and this credential is not one. A signed-in browser
        // session reaches the same endpoint without holding any scope.
        return $"Requires the `{scope}` scope, or a browser session.";
    }
}
