using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Host.Workers;

public sealed class ParseRunMaintenanceWorker(
    IServiceScopeFactory serviceScopeFactory,
    ParseRunWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<ParseRunMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Parse Run maintenance worker is disabled.");
            return;
        }

        logger.LogInformation(
            "Parse Run maintenance worker started with interval {MaintenanceInterval}.",
            options.MaintenanceInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunMaintenanceAsync(stoppingToken);

            try
            {
                await Task.Delay(options.MaintenanceInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var leaseStore = scope.ServiceProvider.GetRequiredService<IParseRunLeaseStore>();
            var stateStore = scope.ServiceProvider.GetRequiredService<IParseRunStateStore>();

            var recoveredClaims = await leaseStore.RequeueExpiredUnstartedClaimsAsync(
                nowUtc,
                options.RecoveryBatchSize,
                cancellationToken);
            var queuedRetries = await stateStore.QueueDueRetriesAsync(
                nowUtc,
                options.RecoveryBatchSize,
                cancellationToken);

            if (recoveredClaims > 0 || queuedRetries > 0)
            {
                logger.LogInformation(
                    "Parse Run maintenance recovered {RecoveredClaims} claims and queued {QueuedRetries} retries.",
                    recoveredClaims,
                    queuedRetries);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Parse Run maintenance cycle failed.");
        }
    }
}
