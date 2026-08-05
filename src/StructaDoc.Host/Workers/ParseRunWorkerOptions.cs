namespace StructaDoc.Host.Workers;

public sealed class ParseRunWorkerOptions
{
    public const string SectionName = "Worker";

    public bool Enabled { get; init; } = true;

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int RecoveryBatchSize { get; init; } = 100;

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
    }
}
