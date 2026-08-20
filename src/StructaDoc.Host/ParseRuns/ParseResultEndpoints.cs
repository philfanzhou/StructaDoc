using Microsoft.Net.Http.Headers;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Host.Authentication;
using StructaDoc.Adapters.Persistence.ParseRuns;

namespace StructaDoc.Host.ParseRuns;

public static class ParseResultEndpoints
{
    // Every response is declared because the handlers return `IResult`, which tells a generated
    // document nothing about what comes back. Undeclared, this whole surface is described as routes
    // that answer with no body, and `/blocks` comes out worse than the rest: with its validation
    // failure declared and its success not, the document says the endpoint returns `400` and
    // nothing else. These are the results the product exists to produce, and their shape is the
    // part of the description an integrator writes code against.
    public static IEndpointRouteBuilder MapParseResultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/parse-runs/{parseRunId:guid}").RequireAuthorization(AuthorizationPolicies.ParsesRead).RequiresDocumentPermission(DocumentPermissions.Read);
        group.MapGet("/pages", ListPagesAsync).Produces<IReadOnlyList<ParsePageResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/blocks", ListBlocksAsync).Produces<ParseBlockListResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/assets", ListAssetsAsync).Produces<IReadOnlyList<ParseAssetResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
        // Declared as a binary stream because the response carries the media type of the stored
        // item, which is whatever the parse produced rather than one type this route could name.
        group.MapGet("/assets/{assetId:guid}/content", DownloadAssetAsync).Produces<Stream>(StatusCodes.Status200OK, contentType: "application/octet-stream").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/artifacts", ListArtifactsAsync).Produces<IReadOnlyList<ParseArtifactResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/artifacts/{artifactId:guid}/content", DownloadArtifactAsync).Produces<Stream>(StatusCodes.Status200OK, contentType: "application/octet-stream").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/markdown", DownloadMarkdownAsync).Produces<Stream>(StatusCodes.Status200OK, contentType: "text/markdown").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/markdown/preview", PreviewMarkdownAsync).Produces<Stream>(StatusCodes.Status200OK, contentType: "text/html").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
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
    /// differ, and both are the reason it is a separate route. It asks for read access rather than
    /// the export permission, which costs nothing to withhold here because this route returns the
    /// export's own bytes: what the export permission separates is the packaged deliverable, not
    /// the content. And it is served inline instead of as an attachment, because a browser that
    /// downloads a preview has not shown one.
    ///
    /// Images are inlined rather than linked at their authorized endpoints because
    /// <c>Content-Security-Policy: sandbox</c> puts the page in an opaque origin, where a request
    /// back to this service carries no session cookie and would load nothing. Inlining is bounded,
    /// so a result whose images exceed the export budget previews with those images missing.
    /// </remarks>
    private static async Task<IResult> PreviewMarkdownAsync(Guid parseRunId, HttpContext context, IParseExportService exports, CancellationToken cancellationToken)
    {
        try
        {
            var access = ResourceAccessContextFactory.Create(context.User);
            var fingerprint = await exports.GetHtmlEntityTagAsync(parseRunId, access, cancellationToken);
            if (fingerprint is null) return NotFound(parseRunId);

            var entityTag = new EntityTagHeaderValue($"\"{fingerprint}\"");
            SetDownloadHeaders(context);
            if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(candidate =>
                    candidate.Tag == "*" || candidate.Compare(entityTag, useStrongComparison: false)) == true)
            {
                context.Response.GetTypedHeaders().ETag = entityTag;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var item = await exports.CreateAsync(parseRunId, "html", access, cancellationToken);
            if (item is null) return NotFound(parseRunId);
            return Results.File(item.Content, item.MediaType, fileDownloadName: null, entityTag: entityTag, enableRangeProcessing: true);
        }
        catch (ParseResultContentUnavailableException)
        {
            return ContentUnavailable();
        }
    }

    private static async Task<IResult> DownloadAsync(Guid parseRunId, HttpContext context, Func<Task<ParseResultContent?>> open, bool inline = false)
    {
        try
        {
            var item = await open();
            if (item is null) return NotFound(parseRunId);
            SetDownloadHeaders(context);
            return Results.File(item.Content, item.MediaType, inline ? null : item.Name, entityTag: new EntityTagHeaderValue($"\"{item.Sha256}\""), enableRangeProcessing: true);
        }
        catch (ParseResultContentUnavailableException)
        {
            return ContentUnavailable();
        }
    }

    private static void SetDownloadHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "sandbox";
    }

    private static IResult ContentUnavailable() => Results.Problem(statusCode: 503, title: "Parse result content unavailable", detail: "The result metadata exists, but its stored content is currently unavailable.");
    private static IResult NotFound(Guid id) => Results.Problem(statusCode: 404, title: "Parse Run or result not found", detail: $"Parse Run '{id:D}' or the requested result does not exist or is not accessible.");
}
