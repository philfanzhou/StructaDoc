using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.OpenApi;

// The machine-readable description of the service API, and a page that browses it.
//
// StructaDoc is integrated with by other systems, and until now the only description of its API was
// prose in this repository. Prose cannot be handed to a code generator, cannot be diffed when an
// endpoint changes, and is not what an integrator has open while writing a request. The document
// below is generated from the endpoints themselves, so it cannot describe a route that does not
// exist or miss one that does.
//
// The document is produced by the platform's own OpenAPI support. What it cannot know is how a
// caller authenticates and which scope each endpoint requires, because that lives in authorization
// policies rather than in the signature; `ApiSecurityTransformer` supplies it.
public static class StructaDocApiDescription
{
    // The contract version, which is also the path segment. It is not the build version: a build
    // that adds an optional field does not change what the caller is coding against. `GET
    // /api/v1/system/info` reports which build is answering.
    public const string DocumentName = "v1";
    public const string BrowserDocumentName = "v1-browser";

    // Under `/api` so the description travels with the API it describes, including through a proxy
    // that publishes only that prefix, and so the Host's client-route fallback already excludes it.
    public const string DocumentRoute = "/api/{documentName}/openapi.json";
    public const string DocumentPath = "/api/v1/openapi.json";
    public const string BrowserDocumentPath = "/api/v1-browser/openapi.json";
    public const string BrowsableRoutePrefix = "api/v1/docs";

    public static IServiceCollection AddStructaDocApiDescription(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Keep the document name as a literal here. The .NET 10 XML-comment source generator only
        // intercepts AddOpenApi overloads whose document-name argument is a literal expression;
        // passing DocumentName would generate the XML file but silently omit it from the document.
        services.AddOpenApi("v1", options =>
        {
            // This is the contract intended for generated clients. Browser-only administration,
            // setup, and session routes use cookies and antiforgery rather than API client keys;
            // including them makes a generated SDK advertise methods it cannot authenticate.
            options.ShouldInclude = IsApiClientOperation;
            options.AddDocumentTransformer<ApiDocumentTransformer>();
            options.AddOperationTransformer<ApiClientContractTransformer>();
            options.AddOperationTransformer<ApiSecurityTransformer>();
            options.AddSchemaTransformer<ApiSchemaTransformer>();
        });

        // Keep a separate description of the entire browser and service surface for operators and
        // for the bundled Swagger UI. It is deliberately not the document consumers generate from.
        // The literal name is required by the XML-comment source generator; see the note above.
        services.AddOpenApi("v1-browser", options =>
        {
            options.ShouldInclude = IsServiceApiOperation;
            options.AddDocumentTransformer<ApiDocumentTransformer>();
            options.AddOperationTransformer<ApiClientContractTransformer>();
            options.AddOperationTransformer<ApiSecurityTransformer>();
            options.AddSchemaTransformer<ApiSchemaTransformer>();
        });

        return services;
    }

    public static WebApplication MapStructaDocApiDescription(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Served without authentication. The description names routes and shapes, and the web
        // application is public static content that already contains every one of them, so a
        // credential here would withhold nothing from anyone who wanted it. What the endpoints do
        // is still authorized on every request.
        app.MapOpenApi(DocumentRoute).AllowAnonymous();

        return app;
    }

    public static WebApplication UseStructaDocApiDescriptionPage(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Swagger UI carries its assets inside the assembly, which is why it is here rather than a
        // script tag: a deployment on an isolated network is the deployment this product is built
        // for, and a page that needs a CDN would be blank in exactly those installations.
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = BrowsableRoutePrefix;
            options.SwaggerEndpoint(DocumentPath, "StructaDoc API clients (v1)");
            options.SwaggerEndpoint(BrowserDocumentPath, "StructaDoc complete browser surface (v1)");
            options.DocumentTitle = "StructaDoc API";
        });

        return app;
    }

    private static bool IsApiClientOperation(ApiDescription description)
    {
        if (!IsServiceApiOperation(description))
        {
            return false;
        }

        // Service identity is intentionally public. Every other consumer operation must be backed
        // by one of the four API-client scope policies, which is the same fact authentication uses.
        if (string.Equals(description.RelativePath, "api/v1/system/info", StringComparison.Ordinal))
        {
            return true;
        }

        return description.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .Any(policy => policy is AuthorizationPolicies.DocumentsRead
                or AuthorizationPolicies.DocumentsWrite
                or AuthorizationPolicies.ParsesRead
                or AuthorizationPolicies.ParsesWrite);
    }

    private static bool IsServiceApiOperation(ApiDescription description) =>
        description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;
}
