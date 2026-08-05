namespace StructaDoc.Application.ParseRuns;

public sealed record ParseRunUnsubmittedRecovery(
    int RequeuedCount,
    int FailedUnknownSubmissionCount);
