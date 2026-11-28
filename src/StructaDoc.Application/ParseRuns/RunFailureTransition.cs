namespace StructaDoc.Application.ParseRuns;

public sealed record RunFailureTransition(
    Guid ParseRunId,
    string Status,
    long ConcurrencyVersion);
