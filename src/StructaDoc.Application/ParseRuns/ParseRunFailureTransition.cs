namespace StructaDoc.Application.ParseRuns;

public sealed record ParseRunFailureTransition(
    Guid ParseRunId,
    string Status,
    long ConcurrencyVersion);
