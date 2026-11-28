namespace StructaDoc.Application.ParseRuns;

public sealed record RunLease(
    Guid ParseRunId,
    string WorkerId,
    long ConcurrencyVersion,
    DateTime LeaseExpiresAtUtc);
