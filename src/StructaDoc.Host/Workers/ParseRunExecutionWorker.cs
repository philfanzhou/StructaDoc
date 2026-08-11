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
        // Worker:Enabled decides whether this Host runs Workers at all and is a deployment choice.
        if (!options.Enabled)
        {
            logger.LogInformation("Parse Run execution worker is disabled.");
            return;
        }

        logger.LogInformation(
            "Parse Run execution worker {WorkerId} started with {MaxConcurrency} slots.",
            workerId,
            options.MaxConcurrency);

        // Each slot claims independently under its own Worker ID, so one long-running Parse Run
        // cannot hold up the others on this Host.
        await Task.WhenAll(Enumerable
            .Range(0, options.MaxConcurrency)
            .Select(slot => RunSlotAsync($"{workerId}:{slot}", stoppingToken)));
    }

    private async Task RunSlotAsync(string slotWorkerId, CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await TryExecuteOneAsync(slotWorkerId, stoppingToken))
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
                    "Parse Run execution cycle failed on {WorkerId} with exception type {ExceptionType}.",
                    slotWorkerId,
                    exception.GetType().FullName);

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
    }

    private async Task<bool> TryExecuteOneAsync(
        string slotWorkerId,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IParseRunLeaseStore>();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var lease = await leaseStore.TryRecoverNextRunningAsync(
            slotWorkerId,
            nowUtc,
            options.LeaseDuration,
            cancellationToken);
        var alreadyRunning = lease is not null;
        lease ??= await leaseStore.TryClaimNextAsync(
            slotWorkerId,
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
