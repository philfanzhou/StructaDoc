namespace StructaDoc.Host.Workers;

public sealed class ParseRunWorkerOptions
{
    public const string SectionName = "Worker";

    public bool Enabled { get; init; } = true;

    public bool ExecutionEnabled { get; init; }

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int RecoveryBatchSize { get; init; } = 100;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MinimumPollDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumPollDelay { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaintenanceInterval < TimeSpan.FromMilliseconds(100))
        {
            throw new InvalidOperationException(
                "Worker:MaintenanceInterval must be at least 100 milliseconds.");
        }

        if (RecoveryBatchSize <= 0)
        {
            throw new InvalidOperationException("Worker:RecoveryBatchSize must be positive.");
        }

        if (LeaseDuration < TimeSpan.FromMilliseconds(500))
        {
            throw new InvalidOperationException(
                "Worker:LeaseDuration must be at least 500 milliseconds.");
        }

        if (HeartbeatInterval < TimeSpan.FromMilliseconds(50))
        {
            throw new InvalidOperationException(
                "Worker:HeartbeatInterval must be at least 50 milliseconds.");
        }

        if (HeartbeatInterval >= LeaseDuration)
        {
            throw new InvalidOperationException(
                "Worker:HeartbeatInterval must be shorter than Worker:LeaseDuration.");
        }

        if (RetryDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Worker:RetryDelay must be positive.");
        }

        if (MinimumPollDelay < TimeSpan.FromMilliseconds(100))
        {
            throw new InvalidOperationException(
                "Worker:MinimumPollDelay must be at least 100 milliseconds.");
        }

        if (MaximumPollDelay < MinimumPollDelay)
        {
            throw new InvalidOperationException(
                "Worker:MaximumPollDelay must not be shorter than Worker:MinimumPollDelay.");
        }
    }
}
