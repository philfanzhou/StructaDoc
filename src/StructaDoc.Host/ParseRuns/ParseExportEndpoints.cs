using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.ParseRuns;

public static class ParseExportEndpoints
{
    private static readonly string[] Formats = ["markdown", "html", "zip", "pdf"];

    public static IEndpointRouteBuilder MapParseExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // One route with four media types, because the format asked for is what comes back.
        endpoints.MapGet("/api/v1/parse-runs/{parseRunId:guid}/exports/{format}", ExportAsync)
            .RequireAuthorization(AuthorizationPolicies.ParsesRead)
            .RequiresDocumentPermission(DocumentPermissions.Export)
            .Produces<Stream>(StatusCodes.Status200OK, contentType: "text/markdown", additionalContentTypes: ["text/html", "application/zip", "application/pdf"])
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            // The four formats are the difference between a caller writing `pdf` and a caller
            // guessing at it, and the route is otherwise described as taking any string at all.
            // They are listed from the array the handler validates against, so a fifth format
            // cannot be accepted by the service and left out of the document.
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Parameters?.FirstOrDefault(parameter => parameter.Name == "format")?.Schema is OpenApiSchema schema)
                {
                    schema.Enum = [.. Formats.Select(format => (JsonNode)format)];
                }

                return Task.CompletedTask;
            });
        return endpoints;
    }

    /// <summary>Creates a packaged Markdown, HTML, ZIP, or PDF deliverable.</summary>
    /// <remarks>
    /// Export permission gates the packaged deliverable, not confidentiality: a caller with read
    /// permission can still retrieve the stored result resources and rendered HTML preview.
    /// </remarks>
    internal static async Task<IResult> ExportAsync(Guid parseRunId, string format, HttpContext context, IParseResultReadService readService, IDocumentAuthorizationService authorization, IParseExportService exports, CancellationToken cancellationToken)
    {
        if (!Formats.Contains(format, StringComparer.OrdinalIgnoreCase)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [$"Format must be one of: {string.Join(", ", Formats)}."] });
        var access = ResourceAccessContextFactory.Create(context.User);
        var run = await readService.GetAsync(parseRunId, access, cancellationToken);
        if (run is null || !await authorization.HasPermissionAsync(run.DocumentId, access, RequiredDocumentPermission.Of(context), cancellationToken)) return Results.Problem(statusCode: 404, title: "Parse Run not found", detail: "The Parse Run does not exist or cannot be exported by the current subject.");
        var export = await exports.CreateAsync(parseRunId, format, access, cancellationToken);
        if (export is null) return Results.Problem(statusCode: 409, title: "Export unavailable", detail: "The requested export is not available for this Parse Run.");
        context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "sandbox";
        return Results.File(export.Content, export.MediaType, export.Name, entityTag: new EntityTagHeaderValue($"\"{export.Sha256}\""), enableRangeProcessing: true);
    }
}
