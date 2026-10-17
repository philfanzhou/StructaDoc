using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Host.Workers;

public sealed class ParseRunExecutionWorker(
    IServiceScopeFactory serviceScopeFactory,
    ParseRunWorkerOptions options,
    WorkerConcurrencyGate gate,
    TimeProvider timeProvider,
    ILogger<ParseRunExecutionWorker> logger) : BackgroundService
{
    private readonly string workerId = CreateWorkerId();
    private int executionCounter;
    private Task executions = Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Worker:Enabled decides whether this Host runs Workers at all and is a deployment choice.
        if (!options.Enabled)
        {
            logger.LogInformation("Parse Run execution worker is disabled.");
            return;
        }

        logger.LogInformation(
            "Parse Run execution worker {WorkerId} started with up to {MaxConcurrency} concurrent executions.",
            workerId,
            options.MaxConcurrency);

        // One dispatch loop claims under the concurrency gate rather than through a fixed set of
        // slot loops, so the ceiling can move while executions are in flight: raising it admits the
        // next claim at once, lowering it holds new claims until running executions finish. Runs
        // already executing are never touched, exactly as the lease model requires.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await gate.WaitAsync(stoppingToken);

                ClaimedExecution? claimed = null;
                try
                {
                    claimed = await TryClaimOneAsync(stoppingToken);
                }
                finally
                {
                    // The permit transfers to the execution when one was claimed; otherwise it goes
                    // straight back, including on the failure paths that never reach the dispatch.
                    if (claimed is null)
                    {
                        gate.Release();
                    }
                }

                if (claimed is null)
                {
                    await Task.Delay(options.MaintenanceInterval, timeProvider, stoppingToken);
                }
                else
                {
                    TrackExecution(RunExecutionAsync(claimed.Value, stoppingToken));
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
                    workerId,
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

        // Shutdown waits out whatever is executing, exactly as the slot loops it replaces did: a
        // claim the database already granted is not something to drop on the floor of a stop. Each
        // execution observes the stopping token through its own lease and deadline handling, so
        // this await is bounded by their cancellation, not by an abandoned task.
        await executions;
    }

    private void TrackExecution(Task execution)
    {
        lock (this)
        {
            executions = Task.WhenAll(executions, execution);
        }
    }

    private async Task<ClaimedExecution?> TryClaimOneAsync(CancellationToken cancellationToken)
    {
        // Each execution claims under its own Worker ID, so one long-running Parse Run cannot hold
        // up the others on this Host, and the lease, its renewal, and the claim rows all keep
        // agreeing on which execution owns them.
        var executionWorkerId = $"{workerId}:{Interlocked.Increment(ref executionCounter)}";

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var leaseStore = scope.ServiceProvider.GetRequiredService<IParseRunLeaseStore>();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var lease = await leaseStore.TryRecoverNextRunningAsync(
            executionWorkerId,
            nowUtc,
            options.LeaseDuration,
            cancellationToken);
        var alreadyRunning = lease is not null;
        lease ??= await leaseStore.TryClaimNextAsync(
            executionWorkerId,
            nowUtc,
            options.LeaseDuration,
            cancellationToken);
        return lease is null ? null : new ClaimedExecution(lease, alreadyRunning);
    }

    private async Task RunExecutionAsync(
        ClaimedExecution claim,
        CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<ParseRunExecutor>()
                .ExecuteAsync(claim.Lease, claim.AlreadyRunning, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Parse Run execution failed on {WorkerId} with exception type {ExceptionType}.",
                claim.Lease.WorkerId,
                exception.GetType().FullName);
        }
        finally
        {
            gate.Release();
        }
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

    private readonly record struct ClaimedExecution(ParseRunLease Lease, bool AlreadyRunning);
}
