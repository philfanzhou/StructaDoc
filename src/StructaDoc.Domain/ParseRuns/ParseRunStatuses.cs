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

    public static readonly string[] Final = [Succeeded, Failed, Cancelled];

    public static readonly string[] Cancellable = [Queued, Claimed, Running, RetryWait];

    public static bool IsFinal(string status) => Final.Contains(status, StringComparer.Ordinal);
}
