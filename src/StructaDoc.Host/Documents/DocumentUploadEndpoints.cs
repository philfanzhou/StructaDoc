using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Storage;
using StructaDoc.Contracts.Documents;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Documents;

public static class DocumentUploadEndpoints
{
    private const long MultipartOverheadAllowance = 1024 * 1024;

    public static RouteHandlerBuilder MapDocumentUpload(
        this IEndpointRouteBuilder endpoints,
        long maxUploadBytes)
    {
        return endpoints.MapPost("/api/v1/documents", UploadDocumentAsync)
            .WithName("UploadDocument")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<DocumentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .RequireAuthorization(AuthorizationPolicies.DocumentsWrite)
            .WithMetadata(new RequestSizeLimitAttribute(
                checked(maxUploadBytes + MultipartOverheadAllowance)));
    }

    private static async Task<IResult> UploadDocumentAsync(
        HttpRequest request,
        IDocumentIngestionService ingestionService,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return UploadProblem(
                StatusCodes.Status415UnsupportedMediaType,
                "multipart-required",
                "The request must use multipart/form-data.");
        }

        try
        {
            if (request.HttpContext.User.HasClaim(
                    StructaDocClaimTypes.SubjectType,
                    SubjectTypes.Administrator))
            {
                await antiforgery.ValidateRequestAsync(request.HttpContext);
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");

            if (file is null || form.Files.Count != 1)
            {
                return UploadProblem(
                    StatusCodes.Status400BadRequest,
                    "single-file-required",
                    "Provide exactly one file in the 'file' form field.");
            }

            await using var content = file.OpenReadStream();
            var document = await ingestionService.IngestAsync(
                new DocumentIngestionRequest(
                    file.FileName,
                    file.ContentType,
                    content,
                    GetActorId(request.HttpContext.User)),
                cancellationToken);
            var response = new DocumentResponse(
                document.Id,
                document.OriginalFileName,
                document.MediaType,
                document.Extension,
                document.SizeBytes,
                document.Sha256,
                document.CreatedAtUtc);

            return Results.Created($"/api/v1/documents/{document.Id:D}", response);
        }
        catch (FileSizeLimitExceededException exception)
        {
            return UploadProblem(
                StatusCodes.Status413PayloadTooLarge,
                "file-too-large",
                exception.Message);
        }
        catch (AntiforgeryValidationException)
        {
            return UploadProblem(
                StatusCodes.Status400BadRequest,
                "invalid-antiforgery-token",
                "A valid antiforgery token is required for administrator uploads.");
        }
        catch (UnsupportedDocumentTypeException exception)
        {
            return UploadProblem(
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported-document-type",
                exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return UploadProblem(
                StatusCodes.Status400BadRequest,
                "invalid-upload",
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            return UploadProblem(
                StatusCodes.Status400BadRequest,
                "invalid-upload",
                exception.Message);
        }
    }

    private static string GetActorId(ClaimsPrincipal user)
    {
        var subjectType = user.FindFirstValue(StructaDocClaimTypes.SubjectType)
            ?? throw new InvalidOperationException("Authenticated subject type is missing.");
        var subjectId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated subject ID is missing.");
        return $"{subjectType}:{subjectId}";
    }

    private static IResult UploadProblem(int statusCode, string code, string detail)
    {
        return Results.Problem(
            statusCode: statusCode,
            title: "Document upload failed",
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });
    }
}
