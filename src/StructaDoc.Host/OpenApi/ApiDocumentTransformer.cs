using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace StructaDoc.Host.OpenApi;

// Everything about the document that is true of the API as a whole rather than of one endpoint: who
// it belongs to, how a caller authenticates, and which group each path falls into.
internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    // The header an API client presents. It is described as an API key rather than as an HTTP
    // authorization scheme because the value includes the `ApiKey ` prefix, and describing it any
    // other way would produce a document whose examples do not authenticate.
    public const string ApiKeySchemeName = "ApiKey";

    // The first prefix that matches wins, so a path that belongs to a group by subject rather than
    // by its leading segments has to be named before the group it nests inside. Starting and listing
    // a document's Parse Runs is the first thing an integrator looks for under Parse Runs, and the
    // last place they would look for it is Documents.
    private static readonly (string Prefix, string Tag)[] Groups =
    [
        ("/api/v1/documents/{documentId}/parse-runs", ApiTags.ParseRuns),
        ("/api/v1/documents", ApiTags.Documents),
        ("/api/v1/parse-runs", ApiTags.ParseRuns),
        ("/api/v1/parse-execution", ApiTags.ParseRuns),
        ("/api/v1/admin", ApiTags.Administration),
        ("/api/v1/session", ApiTags.Sessions),
        ("/api/v1/setup", ApiTags.Sessions),
        ("/api/v1/system", ApiTags.System),
    ];

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "StructaDoc API",
            Version = StructaDocApiDescription.DocumentName,
            Description = Describe(),
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[ApiKeySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "An API client credential, presented with its scheme: `ApiKey <credential>`. "
                + "An administrator creates the client and its scopes under `/admin`; the credential is "
                + "shown once, at creation and at rotation.",
        };

        document.Tags = ApiTags.Describe();
        ApplyTags(document);

        return Task.CompletedTask;
    }

    private static void ApplyTags(OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        foreach (var (path, item) in document.Paths)
        {
            var tag = Groups.FirstOrDefault(
                group => path.StartsWith(group.Prefix, StringComparison.Ordinal)).Tag;
            if (tag is null || item.Operations is null)
            {
                continue;
            }

            foreach (var operation in item.Operations.Values)
            {
                operation.Tags = new HashSet<OpenApiTagReference> { new(tag, document) };
            }
        }
    }

    private static string Describe()
    {
        var build = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        return $"""
            Document ingestion, asynchronous parsing, and normalized structured results.

            This document describes the `v1` contract. Within it, fields are added rather than
            changed, so a client must tolerate fields and Block types it does not know. The build
            answering right now is `{build}`; `GET /api/v1/system/info` reports it at runtime.

            **Authentication.** An application authenticates with an API client credential in the
            `Authorization` header. A scope authorizes the endpoint; ownership or an explicit grant
            authorizes the resource, so a client reaches what it uploaded and what was shared with
            it rather than the whole workspace. A resource outside that boundary answers `404`
            rather than `403`, so holding a credential does not reveal which resource IDs exist.

            Endpoints marked as requiring a browser session are not reachable with an API client
            credential at all, and their writes additionally require the antiforgery token from
            `GET /api/v1/admin/antiforgery` in the `X-CSRF-TOKEN` header. They are described here
            because they are part of the surface, not because an integration should call them.

            Parsing is asynchronous: creating a Parse Run returns immediately and the result is
            polled. Errors are `application/problem+json`.
            """;
    }
}
