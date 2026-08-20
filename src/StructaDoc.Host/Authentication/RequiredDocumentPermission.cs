using StructaDoc.Application.Authentication;

namespace StructaDoc.Host.Authentication;

// The permission a caller must hold on the Document behind a resource, declared on the route beside
// the scope policy.
//
// A scope policy is endpoint metadata, so the API description can read it and state it. A resource
// permission is a question asked of the database partway through the request, and nothing outside
// the code that asks it could see it. That left every Document-scoped route describing half its
// admission rule: `/exports/{format}` said it wanted `parses:read` and stopped there, so a caller
// holding read access and no export permission was answered `404` by an endpoint the description
// had told them to call.
//
// Where the check sits in the handler, the handler reads the requirement back from here rather
// than naming a permission a second time, so the promise and the enforcement cannot drift apart.
// Where it sits behind the service boundary, this declaration reports what that layer requires.
internal sealed record RequiredDocumentPermission(DocumentPermissions Permission)
{
    public static DocumentPermissions Of(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        return endpoint?.Metadata.GetMetadata<RequiredDocumentPermission>()?.Permission
            ?? throw new InvalidOperationException(
                $"Endpoint '{endpoint?.DisplayName ?? context.Request.Path.Value}' checks a Document "
                + "permission without declaring one, so the API description cannot state it.");
    }
}

internal static class RequiredDocumentPermissionExtensions
{
    public static TBuilder RequiresDocumentPermission<TBuilder>(
        this TBuilder builder,
        DocumentPermissions permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new RequiredDocumentPermission(permission)));
        return builder;
    }
}
