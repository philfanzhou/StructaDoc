namespace StructaDoc.Application.ParseRuns;

public sealed record RunUnsubmittedRecovery(
    int RequeuedCount,
    int FailedUnknownSubmissionCount);
