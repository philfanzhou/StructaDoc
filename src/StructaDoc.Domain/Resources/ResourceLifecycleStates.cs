namespace StructaDoc.Domain.Resources;

public static class ResourceLifecycleStates
{
    public const string Active = "active";
    public const string DeletionPending = "deletion-pending";
    public const string DeletionFailed = "deletion-failed";
}

public static class CleanupJobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string RetryWait = "retry-wait";
    public const string Completed = "completed";
}

public static class CleanupTargetTypes
{
    public const string Document = "document";
    public const string ParseRun = "parse-run";
}
