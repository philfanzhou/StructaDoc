using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Primitives;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Contracts.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.Authentication;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.ParseRuns;

public static class ParseRunEndpoints
{
    private const int DefaultMaxAttempts = 3;
    private const int MaximumMaxAttempts = 10;
    private const int MaximumOptionsBytes = 16 * 1024;
    private const int MaximumIdempotencyKeyLength = 256;

    public static IEndpointRouteBuilder MapParseRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/documents/{documentId:guid}/parse-runs", CreateAsync)
            .RequireAuthorization(AuthorizationPolicies.ParsesWrite)
            .Produces<ParseRunResponse>(StatusCodes.Status201Created)
            .Produces<ParseRunResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapGet("/api/v1/parse-runs/{id:guid}", GetAsync)
            .RequireAuthorization(AuthorizationPolicies.ParsesRead)
            .Produces<ParseRunResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapGet("/api/v1/documents/{documentId:guid}/parse-runs", ListForDocumentAsync)
            .RequireAuthorization(AuthorizationPolicies.ParsesRead);
        endpoints.MapPost("/api/v1/parse-runs/{id:guid}/cancel", CancelAsync)
            .RequireAuthorization(AuthorizationPolicies.ParsesWrite)
            .Produces<ParseRunResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        // Queueing work before execution is turned on is a supported thing to do, so a run is
        // accepted either way. What is not supportable is leaving the person watching it unable to
        // tell a queue that is moving from one that never will. Anyone allowed to read Parse Runs
        // may read this, because whoever is waiting is usually not the administrator who can act.
        endpoints.MapGet("/api/v1/parse-execution", GetExecutionStatus)
            .RequireAuthorization(AuthorizationPolicies.ParsesRead)
            .Produces<ParseExecutionStatusResponse>();
        return endpoints;
    }

    // The gate rather than the bound option: an administrator can open it while the service runs,
    // and a page that reported the startup value would keep saying parsing is off after it was on.
    private static IResult GetExecutionStatus(
        ParseRunWorkerOptions options,
        ParseExecutionGate executionGate) =>
        Results.Ok(new ParseExecutionStatusResponse(options.Enabled, executionGate.IsOpen));

    private static async Task<IResult> CancelAsync(
        Guid id,
        HttpContext context,
        IAntiforgery antiforgery,
        IParseRunService service,
        IParseResultReadService readService,
        StructaDoc.Application.Documents.IDocumentAuthorizationService authorizationService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!context.User.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.ApiClient))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Antiforgery validation failed",
                    detail: "A valid antiforgery token is required for administrator requests.");
            }
        }

        var access = ResourceAccessContextFactory.Create(context.User);
        var parseRun = await readService.GetAsync(id, access, cancellationToken);
        if (parseRun is null
            || !await authorizationService.HasPermissionAsync(
                parseRun.DocumentId,
                access,
                StructaDoc.Application.Authentication.DocumentPermissions.Parse,
                cancellationToken))
        {
            return NotFound(id);
        }

        var result = await service.RequestCancellationAsync(
            id,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        if (result.Status == ParseRunCancellationStatus.NotFound || result.ParseRun is null)
        {
            return NotFound(id);
        }

        // Cancellation can complete before this response is built, so an already cancelled run
        // satisfies the caller's intent and stays idempotent. Only a run that reached a different
        // final state is a genuine conflict.
        if (result.ParseRun.Status == ParseRunStatuses.Cancelled)
        {
            return Results.Accepted($"/api/v1/parse-runs/{id:D}", ToResponse(result.ParseRun));
        }

        return result.Status switch
        {
            ParseRunCancellationStatus.Requested or ParseRunCancellationStatus.AlreadyRequested =>
                Results.Accepted(
                    $"/api/v1/parse-runs/{id:D}",
                    ToResponse(result.ParseRun)),
            ParseRunCancellationStatus.AlreadyFinal => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Parse Run is already final",
                detail: $"Parse Run '{id:D}' has status '{result.ParseRun.Status}' and cannot be cancelled."),
            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Parse Run cannot be cancelled",
                detail: "The Parse Run changed concurrently. Retry the cancellation."),
        };
    }

    private static IResult NotFound(Guid id) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Parse Run not found",
        detail: $"Parse Run '{id:D}' does not exist or is not accessible.");

    private static async Task<IResult> CreateAsync(
        Guid documentId,
        StructaDoc.Contracts.ParseRuns.ParseRunCreateRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        IParseRunService service,
        StructaDoc.Application.Documents.IDocumentAuthorizationService authorizationService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!context.User.HasClaim(StructaDocClaimTypes.SubjectType, SubjectTypes.ApiClient))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Antiforgery validation failed",
                    detail: "A valid antiforgery token is required for administrator requests.");
            }
        }

        if (!TryNormalizeOptions(request.Options, out var optionsJson, out var optionsError))
        {
            return Validation("options", optionsError);
        }

        var maxAttempts = request.MaxAttempts ?? DefaultMaxAttempts;
        if (maxAttempts is < 1 or > MaximumMaxAttempts)
        {
            return Validation("maxAttempts", $"Max attempts must be between 1 and {MaximumMaxAttempts}.");
        }

        if (!TryGetIdempotencyKey(context.Request.Headers["Idempotency-Key"], out var idempotencyKey))
        {
            return Validation(
                "Idempotency-Key",
                $"Idempotency-Key must be a single visible ASCII value up to {MaximumIdempotencyKeyLength} characters.");
        }

        if (!await authorizationService.HasPermissionAsync(
                documentId,
                ResourceAccessContextFactory.Create(context.User),
                StructaDoc.Application.Authentication.DocumentPermissions.Parse,
                cancellationToken))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Document not found",
                detail: $"Document '{documentId:D}' does not exist or is not accessible.");
        }

        var result = await service.CreateAsync(
            new StructaDoc.Application.ParseRuns.ParseRunCreateRequest(
                documentId,
                request.ProviderConfigId,
                optionsJson,
                maxAttempts,
                ResourceAccessContextFactory.GetActorId(context.User),
                idempotencyKey,
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);

        return result.Status switch
        {
            ParseRunCreationStatus.Created => Results.Created(
                $"/api/v1/parse-runs/{result.ParseRun!.Id:D}", ToResponse(result.ParseRun)),
            ParseRunCreationStatus.Replayed => Replay(context, result.ParseRun!),
            ParseRunCreationStatus.DocumentNotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Document not found",
                detail: $"Document '{documentId:D}' does not exist."),
            ParseRunCreationStatus.ProviderConfigNotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Provider Config not found",
                detail: $"Provider Config '{request.ProviderConfigId:D}' does not exist."),
            ParseRunCreationStatus.ProviderUnavailable => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Provider unavailable",
                detail: "No enabled Provider Config is available for this Parse Run."),
            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Parse Run cannot be created",
                detail: "The Parse Run conflicts with current state."),
        };
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext context,
        IParseResultReadService service,
        CancellationToken cancellationToken)
    {
        var parseRun = await service.GetAsync(id, ResourceAccessContextFactory.Create(context.User), cancellationToken);
        return parseRun is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Parse Run not found",
                detail: $"Parse Run '{id:D}' does not exist.")
            : Results.Ok(ToResponse(parseRun));
    }

    private static async Task<IResult> ListForDocumentAsync(Guid documentId, HttpContext context, IParseResultReadService service, StructaDoc.Application.Documents.IDocumentAuthorizationService authorizationService, CancellationToken cancellationToken)
    {
        var access = ResourceAccessContextFactory.Create(context.User);
        if (!await authorizationService.HasPermissionAsync(documentId, access, DocumentPermissions.Read, cancellationToken))
        {
            return Results.Problem(statusCode: 404, title: "Document not found", detail: $"Document '{documentId:D}' does not exist or is not accessible.");
        }
        var runs = await service.ListForDocumentAsync(documentId, access, cancellationToken);
        return Results.Ok(runs.Select(ToResponse));
    }

    private static bool TryNormalizeOptions(JsonElement? options, out string json, out string error)
    {
        var value = options ?? JsonSerializer.Deserialize<JsonElement>("{}");
        if (value.ValueKind != JsonValueKind.Object)
        {
            json = string.Empty;
            error = "Options must be a JSON object.";
            return false;
        }

        if (ContainsCredentialField(value))
        {
            json = string.Empty;
            error = "Options cannot contain credential, password, secret, token, API key, or authorization fields.";
            return false;
        }

        json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > MaximumOptionsBytes)
        {
            error = $"Options cannot exceed {MaximumOptionsBytes} UTF-8 bytes.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ContainsCredentialField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalizedName = new string(property.Name
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant)
                    .ToArray());
                if (normalizedName is "authorization"
                    or "credential"
                    or "credentials"
                    or "password"
                    or "secret"
                    or "token"
                    or "apikey"
                    or "accesstoken"
                    or "refreshtoken"
                    || ContainsCredentialField(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsCredentialField);
        }

        return false;
    }

    private static bool TryGetIdempotencyKey(StringValues values, out string? key)
    {
        key = null;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumIdempotencyKeyLength
            || value.Any(character => character is < (char)0x21 or > (char)0x7e))
        {
            return false;
        }

        key = value;
        return true;
    }

    private static string GetActorId(ClaimsPrincipal user)
    {
        var subjectType = user.FindFirstValue(StructaDocClaimTypes.SubjectType)
            ?? throw new InvalidOperationException("Authenticated subject type is missing.");
        var subjectId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated subject ID is missing.");
        return $"{subjectType}:{subjectId}";
    }

    private static ParseRunResponse ToResponse(ParseRunRecord parseRun)
    {
        using var options = JsonDocument.Parse(parseRun.OptionsJson);
        return new ParseRunResponse(
            parseRun.Id,
            parseRun.DocumentId,
            parseRun.Status,
            parseRun.Stage,
            parseRun.ProviderType,
            parseRun.ProviderConfigId,
            parseRun.ProviderConfigVersionId,
            options.RootElement.Clone(),
            parseRun.SourceMediaType,
            parseRun.SubmittedMediaType,
            parseRun.AttemptCount,
            parseRun.MaxAttempts,
            parseRun.NextAttemptAtUtc,
            parseRun.ErrorCode,
            parseRun.ErrorMessage,
            parseRun.CreatedAtUtc,
            parseRun.StartedAtUtc,
            parseRun.CompletedAtUtc);
    }

    private static IResult Replay(HttpContext context, ParseRunRecord parseRun)
    {
        context.Response.Headers["Idempotency-Replayed"] = "true";
        return Results.Ok(ToResponse(parseRun));
    }

    private static IResult Validation(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });
}
