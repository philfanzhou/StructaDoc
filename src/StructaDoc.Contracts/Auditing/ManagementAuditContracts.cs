namespace StructaDoc.Contracts.Auditing;

/// <summary>
/// One management audit entry as the query API returns it. The fields are the audit domain's
/// public identity: what happened (<paramref name="Action"/>), to what
/// (<paramref name="TargetType"/> and <paramref name="TargetId"/>), who did it
/// (<paramref name="OperatorSource"/> and <paramref name="OperatorId"/>), with what
/// <paramref name="Outcome"/>, and when (<paramref name="OccurredAtUtc"/>), plus the bounded
/// <paramref name="Description"/> the record point wrote. No database-internal or storage
/// reference is part of the shape.
/// </summary>
public sealed record ManagementAuditEntryResponse(
    Guid Id,
    string Action,
    string TargetType,
    string TargetId,
    string OperatorSource,
    string OperatorId,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    string? Description);

/// <summary>
/// One page of the management audit trail.
///
/// <paramref name="ContinuationCursor"/> is opaque and bound to the filters, the page size, and
/// the next page: pass it back unchanged with the same filters, the same page size, and
/// <paramref name="Page"/> plus one. Changing any of those between pages is refused rather than
/// silently reinterpreting the cursor. <paramref name="HasNextPage"/> being false means the trail
/// for the current filters is exhausted.
///
/// <paramref name="TotalCount"/> is the count observed while the query executed, not a snapshot:
/// rows written concurrently may make it drift between pages, and a backfilled row whose ordering
/// position lies after the cursor can still appear on a later page.
/// </summary>
public sealed record ManagementAuditPageResponse(
    IReadOnlyList<ManagementAuditEntryResponse> Items,
    int Page,
    int PageSize,
    long TotalCount,
    string? ContinuationCursor,
    bool HasNextPage);
