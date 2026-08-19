using Microsoft.Net.Http.Headers;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Host.Authentication;
using StructaDoc.Adapters.Persistence.ParseRuns;

namespace StructaDoc.Host.ParseRuns;

public static class ParseResultEndpoints
{
    public static IEndpointRouteBuilder MapParseResultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/parse-runs/{parseRunId:guid}").RequireAuthorization(AuthorizationPolicies.ParsesRead);
        group.MapGet("/pages", ListPagesAsync);
        group.MapGet("/blocks", ListBlocksAsync).ProducesValidationProblem();
        group.MapGet("/assets", ListAssetsAsync);
        group.MapGet("/assets/{assetId:guid}/content", DownloadAssetAsync);
        group.MapGet("/artifacts", ListArtifactsAsync);
        group.MapGet("/artifacts/{artifactId:guid}/content", DownloadArtifactAsync);
        group.MapGet("/markdown", DownloadMarkdownAsync);
        group.MapGet("/markdown/preview", PreviewMarkdownAsync);
        return endpoints;
    }

    private static async Task<IResult> ListPagesAsync(Guid parseRunId, HttpContext context, IParseResultReadService service, CancellationToken cancellationToken)
    {
        var records = await service.ListPagesAsync(parseRunId, ResourceAccessContextFactory.Create(context.User), cancellationToken);
        return records is null ? NotFound(parseRunId) : Results.Ok(records.Select(page => new ParsePageResponse(page.Number, page.Width, page.Height, page.Unit)));
    }

    private static async Task<IResult> ListBlocksAsync(Guid parseRunId, int? limit, int? afterSequence, int? pageNumber, HttpContext context, IParseResultReadService service, CancellationToken cancellationToken)
    {
        var take = limit ?? 200;
        if (take is < 1 or > 1000) return Results.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = ["Limit must be between 1 and 1000."] });
        if (afterSequence is < 0 || pageNumber is < 1) return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Sequence must be non-negative and page number must be positive."] });
        var page = await service.ListBlocksAsync(parseRunId, ResourceAccessContextFactory.Create(context.User), take, afterSequence, pageNumber, cancellationToken);
        return page is null ? NotFound(parseRunId) : Results.Ok(new ParseBlockListResponse(page.Items.Select(block => new ParseBlockResponse(block.Id, block.Sequence, block.PageNumber, block.Type, block.Subtype, block.Content, block.ContentFormat, block.BoundingBox is null ? null : new BoundingBoxResponse(block.BoundingBox.X0, block.BoundingBox.Y0, block.BoundingBox.X1, block.BoundingBox.Y1), block.Confidence, block.AssetId)).ToArray(), page.NextSequence));
    }

    private static async Task<IResult> ListAssetsAsync(Guid parseRunId, HttpContext context, IParseResultReadService service, CancellationToken cancellationToken)
    {
        var records = await service.ListAssetsAsync(parseRunId, ResourceAccessContextFactory.Create(context.User), cancellationToken);
        return records is null ? NotFound(parseRunId) : Results.Ok(records.Select(asset => new ParseAssetResponse(asset.Id, asset.Name, asset.MediaType, asset.SizeBytes, asset.Sha256, asset.Width, asset.Height)));
    }

    private static async Task<IResult> ListArtifactsAsync(Guid parseRunId, HttpContext context, IParseResultReadService service, CancellationToken cancellationToken)
    {
        var records = await service.ListArtifactsAsync(parseRunId, ResourceAccessContextFactory.Create(context.User), cancellationToken);
        return records is null ? NotFound(parseRunId) : Results.Ok(records.Select(artifact => new ParseArtifactResponse(artifact.Id, artifact.Type, artifact.Name, artifact.MediaType, artifact.SizeBytes, artifact.Sha256, artifact.CreatedAtUtc)));
    }

    private static Task<IResult> DownloadAssetAsync(Guid parseRunId, Guid assetId, HttpContext context, IParseResultReadService service, CancellationToken cancellationToken) => DownloadAsync(parseRunId, context, () => service.OpenAssetAsync(parseRunId, assetId, ResourceAccessContextFactory.Create(context.User), cancellationToken));
    private static Task<IResult> DownloadArtifactAsync(Guid parseRunId, Guid artifactId, HttpContext context, IParseResultReadService service, CancellationToken cancellationToken) => DownloadAsync(parseRunId, context, () => service.OpenArtifactAsync(parseRunId, artifactId, ResourceAccessContextFactory.Create(context.User), cancellationToken));
    private static Task<IResult> DownloadMarkdownAsync(Guid parseRunId, HttpContext context, IParseResultReadService service, CancellationToken cancellationToken) => DownloadAsync(parseRunId, context, () => service.OpenMarkdownAsync(parseRunId, ResourceAccessContextFactory.Create(context.User), cancellationToken), inline: true);

    /// <summary>
    /// The Markdown result rendered as a self-contained HTML page, for display rather than for
    /// saving.
    /// </summary>
    /// <remarks>
    /// The page is byte-for-byte the HTML export, which is what makes this cheap: the same renderer,
    /// the same Provider-relative link rewriting, and the same bounded image inlining. Two things
    /// differ, and both are the reason it is a separate route. Reading a result is not exporting it,
    /// so this asks for read access rather than the export permission; and it is served inline
    /// instead of as an attachment, because a browser that downloads a preview has not shown one.
    ///
    /// Images are inlined rather than linked at their authorized endpoints because
    /// <c>Content-Security-Policy: sandbox</c> puts the page in an opaque origin, where a request
    /// back to this service carries no session cookie and would load nothing. Inlining is bounded,
    /// so a result whose images exceed the export budget previews with those images missing.
    /// </remarks>
    private static Task<IResult> PreviewMarkdownAsync(Guid parseRunId, HttpContext context, IParseExportService exports, CancellationToken cancellationToken) => DownloadAsync(parseRunId, context, () => exports.CreateAsync(parseRunId, "html", ResourceAccessContextFactory.Create(context.User), cancellationToken), inline: true);

    private static async Task<IResult> DownloadAsync(Guid parseRunId, HttpContext context, Func<Task<ParseResultContent?>> open, bool inline = false)
    {
        try
        {
            var item = await open();
            if (item is null) return NotFound(parseRunId);
            context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "sandbox";
            return Results.File(item.Content, item.MediaType, inline ? null : item.Name, entityTag: new EntityTagHeaderValue($"\"{item.Sha256}\""), enableRangeProcessing: true);
        }
        catch (ParseResultContentUnavailableException)
        {
            return Results.Problem(statusCode: 503, title: "Parse result content unavailable", detail: "The result metadata exists, but its stored content is currently unavailable.");
        }
    }

    private static IResult NotFound(Guid id) => Results.Problem(statusCode: 404, title: "Parse Run or result not found", detail: $"Parse Run '{id:D}' or the requested result does not exist or is not accessible.");
}
