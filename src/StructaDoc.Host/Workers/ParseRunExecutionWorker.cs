using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Host.Workers;

public sealed class ParseRunExecutionWorker(
    IServiceScopeFactory serviceScopeFactory,
    ParseRunWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<ParseRunExecutionWorker> logger) : BackgroundService
{
    private readonly string workerId = CreateWorkerId();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !options.ExecutionEnabled)
        {
            logger.LogInformation("Parse Run execution worker is disabled.");
            return;
        }

        logger.LogInformation("Parse Run execution worker {WorkerId} started.", workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await TryExecuteOneAsync(stoppingToken))
                {
                    await Task.Delay(options.MaintenanceInterval, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Parse Run execution cycle failed with exception type {ExceptionType}.",
                    exception.GetType().FullName);
                await Task.Delay(options.MaintenanceInterval, timeProvider, stoppingToken);
            }
        }
    }

    private async Task<bool> TryExecuteOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IParseRunLeaseStore>();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var lease = await leaseStore.TryRecoverNextRunningAsync(
            workerId,
            nowUtc,
            options.LeaseDuration,
            cancellationToken);
        var alreadyRunning = lease is not null;
        lease ??= await leaseStore.TryClaimNextAsync(
            workerId,
            nowUtc,
            options.LeaseDuration,
            cancellationToken);
        if (lease is null)
        {
            return false;
        }

        await scope.ServiceProvider
            .GetRequiredService<ParseRunExecutor>()
            .ExecuteAsync(lease, alreadyRunning, cancellationToken);
        return true;
    }

    private static string CreateWorkerId()
    {
        var machine = Environment.MachineName;
        if (machine.Length > 80)
        {
            machine = machine[..80];
        }

        return $"{machine}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }
}
