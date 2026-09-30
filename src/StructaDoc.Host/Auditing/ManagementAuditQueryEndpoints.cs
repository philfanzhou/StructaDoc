using ServiceMantle.Audit;
using StructaDoc.Contracts.Auditing;
using StructaDoc.Host.Authentication;

namespace StructaDoc.Host.Auditing;

public static class ManagementAuditQueryEndpoints
{
    public static IEndpointRouteBuilder MapManagementAuditQueryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin/audit")
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        // Read-only: no antiforgery, like every other administration GET.
        group.MapGet("", QueryAsync)
            .Produces<ManagementAuditPageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    /// <summary>Queries the management audit trail.</summary>
    /// <remarks>
    /// <para>
    /// Filters: <c>action</c>, <c>targetType</c>, <c>operator</c> (the operator identifier), and a
    /// UTC time range <c>from</c>/<c>to</c> of at most 366 days. Entries are returned newest first.
    /// </para>
    /// <para>
    /// Pagination is cursor-based. <c>pageSize</c> defaults to 50 and accepts 1 through 200. The
    /// first page is <c>page=1</c> without a cursor; each response's <c>continuationCursor</c> is
    /// opaque and bound to the filters, the page size, and the next page, so pass it back unchanged
    /// with the same filters, the same page size, and <c>page</c> set to the returned page plus
    /// one. Reusing a cursor with different filters, a different page size, or an unexpected page
    /// is refused with a stable error code rather than silently reinterpreted. An offset beyond
    /// the cursor is not offered.
    /// </para>
    /// <para>
    /// <c>totalCount</c> is the count observed while the query executed, not a snapshot: rows
    /// written concurrently may make it drift between pages, and a backfilled row whose ordering
    /// position lies after the cursor can still appear on a later page.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> QueryAsync(
        string? action,
        string? targetType,
        string? @operator,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? pageSize,
        string? cursor,
        IManagementAuditQueryService queryService,
        CancellationToken cancellationToken)
    {
        ManagementAuditAction? parsedAction = null;
        ManagementAuditTargetType? parsedTargetType = null;
        try
        {
            // Parse before constructing the query so an unrecognized filter value is a client
            // error with the library's stable code rather than a binding failure.
            parsedAction = action is null ? null : ManagementAuditAction.Parse(action);
            parsedTargetType = targetType is null ? null : ManagementAuditTargetType.Parse(targetType);

            var query = ManagementAuditQuery.Create(
                action: parsedAction,
                targetType: parsedTargetType,
                targetId: null,
                operatorId: @operator,
                fromUtc: from,
                toUtc: to,
                page: page ?? 1,
                pageSize: pageSize ?? ManagementAuditQuery.DefaultPageSize,
                // The endpoint exposes one fixed order: newest first, the order an administrator
                // reads a trail in. A sort-order parameter would only add a second cursor family.
                sortOrder: ManagementAuditSortOrder.Newest,
                cursor: cursor);

            var result = await queryService.QueryAsync(query, cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (ManagementAuditException exception)
        {
            return IsClientError(exception.ErrorCode)
                ? Problem(StatusCodes.Status400BadRequest, exception)
                : Problem(StatusCodes.Status500InternalServerError, exception);
        }
    }

    // A query the caller composed wrongly is theirs to fix: an unknown action or target type, an
    // out-of-range page or page size, an impossible time range, or a cursor that does not belong
    // to this query. Everything else — a legacy row the query boundary refuses to return whole —
    // is a data problem the caller cannot fix by asking differently.
    private static bool IsClientError(string errorCode) =>
        errorCode.StartsWith("audit.query_", StringComparison.Ordinal)
            || errorCode is "audit.action_invalid" or "audit.target_type_invalid";

    private static IResult Problem(int statusCode, ManagementAuditException exception)
    {
        return Results.Problem(
            statusCode: statusCode,
            title: "The audit query was refused",
            detail: exception.Message,
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = exception.ErrorCode,
            });
    }

    private static ManagementAuditPageResponse ToResponse(ManagementAuditQueryResult result)
    {
        return new ManagementAuditPageResponse(
            result.Items.Select(ToResponse).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount,
            result.ContinuationCursor,
            result.HasNextPage);
    }

    private static ManagementAuditEntryResponse ToResponse(ManagementAuditRecord record)
    {
        return new ManagementAuditEntryResponse(
            record.Id,
            record.Action.Value,
            record.Target.Type.Value,
            record.Target.Id,
            record.Operator.Source.Value,
            record.Operator.OperatorId ?? string.Empty,
            record.Outcome.ToString(),
            record.OccurredAtUtc,
            record.SecurityDescription);
    }
}
