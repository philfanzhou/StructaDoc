namespace StructaDoc.Application.ParseRuns;

public sealed record ParseRunLease(
    Guid ParseRunId,
    string WorkerId,
    long ConcurrencyVersion,
    DateTime LeaseExpiresAtUtc);
