namespace StructaDoc.Host.Workers;

public sealed class ParseRunWorkerOptions
{
    public const string SectionName = "Worker";

    public bool Enabled { get; init; } = true;

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int RecoveryBatchSize { get; init; } = 100;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

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
    }
}
