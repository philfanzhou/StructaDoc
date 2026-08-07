using Microsoft.Net.Http.Headers;
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
        endpoints.MapGet("/api/v1/parse-runs/{parseRunId:guid}/exports/{format}", ExportAsync).RequireAuthorization(AuthorizationPolicies.ParsesRead);
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(Guid parseRunId, string format, HttpContext context, IParseResultReadService readService, IDocumentAuthorizationService authorization, IParseExportService exports, CancellationToken cancellationToken)
    {
        if (!Formats.Contains(format, StringComparer.OrdinalIgnoreCase)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [$"Format must be one of: {string.Join(", ", Formats)}."] });
        var access = ResourceAccessContextFactory.Create(context.User);
        var run = await readService.GetAsync(parseRunId, access, cancellationToken);
        if (run is null || !await authorization.HasPermissionAsync(run.DocumentId, access, DocumentPermissions.Export, cancellationToken)) return Results.Problem(statusCode: 404, title: "Parse Run not found", detail: "The Parse Run does not exist or cannot be exported by the current subject.");
        var export = await exports.CreateAsync(parseRunId, format, access, cancellationToken);
        if (export is null) return Results.Problem(statusCode: 409, title: "Export unavailable", detail: "The requested export is not available for this Parse Run.");
        context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "sandbox";
        return Results.File(export.Content, export.MediaType, export.Name, entityTag: new EntityTagHeaderValue($"\"{export.Sha256}\""), enableRangeProcessing: true);
    }
}
