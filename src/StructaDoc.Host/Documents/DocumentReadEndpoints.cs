using Microsoft.Net.Http.Headers;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Authentication;
using StructaDoc.Contracts.Documents;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Documents;

public static class DocumentReadEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 200;

    public static IEndpointRouteBuilder MapDocumentReadEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/documents")
            .RequireAuthorization(AuthorizationPolicies.DocumentsRead);

        group.MapGet("", ListAsync)
            .Produces<DocumentListResponse>()
            .ProducesValidationProblem();
        group.MapGet("/{id:guid}", GetAsync)
            .RequiresDocumentPermission(DocumentPermissions.Read)
            .Produces<DocumentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{id:guid}/content", DownloadAsync)
            .RequiresDocumentPermission(DocumentPermissions.Read)
            .Produces<Stream>(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        int? limit,
        string? cursor,
        string? fileName,
        string? parseStatus,
        HttpContext context,
        IDocumentReadService service,
        CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;

        if (pageSize is < 1 or > MaximumPageSize)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["limit"] = [$"Limit must be between 1 and {MaximumPageSize}."],
                });
        }

        DocumentCursor? decodedCursor = null;

        if (cursor is not null && !DocumentCursorCodec.TryDecode(cursor, out decodedCursor))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["cursor"] = ["Cursor is invalid."],
                });
        }

        var page = await service.ListAccessibleAsync(
            pageSize,
            ResourceAccessContextFactory.Create(context.User),
            decodedCursor,
            fileName,
            parseStatus,
            cancellationToken);
        return Results.Ok(new DocumentListResponse(
            page.Items.Select(ToResponse).ToArray(),
            page.NextCursor is null ? null : DocumentCursorCodec.Encode(page.NextCursor)));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext context,
        IDocumentReadService service,
        CancellationToken cancellationToken)
    {
        var document = await service.GetAccessibleAsync(
            id,
            ResourceAccessContextFactory.Create(context.User),
            cancellationToken);
        return document is null
            ? NotFound(id)
            : Results.Ok(ToResponse(document));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        HttpContext context,
        IDocumentReadService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await service.OpenAccessibleContentAsync(
                id,
                ResourceAccessContextFactory.Create(context.User),
                cancellationToken);

            if (content is null)
            {
                return NotFound(id);
            }

            context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "sandbox";
            return Results.File(
                content.Content,
                contentType: content.Document.MediaType,
                fileDownloadName: content.Document.OriginalFileName,
                lastModified: new DateTimeOffset(content.Document.CreatedAtUtc),
                entityTag: new EntityTagHeaderValue($"\"{content.Document.Sha256}\""),
                enableRangeProcessing: true);
        }
        catch (DocumentContentUnavailableException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Document content unavailable",
                detail: "The Document exists, but its stored content is currently unavailable.");
        }
    }

    private static DocumentResponse ToResponse(DocumentRecord document)
    {
        return new DocumentResponse(
            document.Id,
            document.OriginalFileName,
            document.MediaType,
            document.Extension,
            document.SizeBytes,
            document.Sha256,
            document.CreatedAtUtc,
            document.LatestParseStatus,
            document.OwnedByCurrentUser);
    }

    private static IResult NotFound(Guid id)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Document not found",
            detail: $"Document '{id:D}' does not exist.");
    }
}
