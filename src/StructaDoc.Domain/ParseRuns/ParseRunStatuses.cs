namespace StructaDoc.Domain.ParseRuns;

public static class ParseRunStatuses
{
    public const string Queued = "queued";
    public const string Claimed = "claimed";
    public const string Running = "running";
    public const string RetryWait = "retry-wait";
    public const string CancelRequested = "cancel-requested";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
