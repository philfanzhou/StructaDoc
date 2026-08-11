namespace StructaDoc.Host.Workers;

public sealed class ParseRunWorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Whether this Host runs Parse Run Workers at all. A deployment choice: it exists so a Host can
    /// be run to serve the API while other Hosts do the parsing, not as a way to pause parsing.
    /// Nothing sends a document anywhere until an administrator configures a Provider, which is the
    /// point where that consent is actually given.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int RecoveryBatchSize { get; init; } = 100;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MinimumPollDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumPollDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Parse Runs this Host executes at the same time. Each slot claims independently, so raising
    /// it must stay within Provider rate limits and the separate LibreOffice conversion limit.
    /// </summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>
    /// Upper bound on one execution attempt, including Provider polling. <see cref="TimeSpan.Zero"/>
    /// disables the bound. Exceeding it ends the attempt as a retriable failure so one unresponsive
    /// Provider cannot hold a slot, and its Document, indefinitely.
    /// </summary>
    public TimeSpan MaxExecutionDuration { get; init; } = TimeSpan.FromHours(1);

    public bool HasExecutionDeadline => MaxExecutionDuration > TimeSpan.Zero;

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

        if (MaxConcurrency is < 1 or > 64)
        {
            throw new InvalidOperationException(
                "Worker:MaxConcurrency must be between 1 and 64.");
        }

        if (MaxExecutionDuration < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Worker:MaxExecutionDuration cannot be negative. Use 00:00:00 to disable the bound.");
        }

        // The deadline may be shorter or longer than the lease. Shorter guarantees the lease is
        // still valid when the timeout is recorded; longer relies on the heartbeat, which the
        // interval check above already keeps ahead of lease expiry.
        if (HasExecutionDeadline && MaxExecutionDuration < TimeSpan.FromSeconds(1))
        {
            throw new InvalidOperationException(
                "Worker:MaxExecutionDuration must be at least 1 second when enabled.");
        }
    }
}
