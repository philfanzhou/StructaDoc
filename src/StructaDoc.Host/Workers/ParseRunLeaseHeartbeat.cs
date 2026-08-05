using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;

namespace StructaDoc.Host.Workers;

public sealed class ParseRunLeaseHeartbeat(
    IServiceScopeFactory serviceScopeFactory,
    ParseRunWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<ParseRunLeaseHeartbeat> logger)
{
    public ParseRunLeaseSession StartSession(
        ParseRunLease currentLease,
        CancellationToken stoppingToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLease.WorkerId);

        if (currentLease.LeaseExpiresAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Lease expiry must use UTC.", nameof(currentLease));
        }

        if (currentLease.LeaseExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
        {
            throw new ArgumentException("Cannot start a heartbeat for an expired lease.", nameof(currentLease));
        }

        return new ParseRunLeaseSession(
            serviceScopeFactory,
            options,
            timeProvider,
            logger,
            currentLease,
            stoppingToken);
    }
}

public sealed class ParseRunLeaseSession : IAsyncDisposable
{
    private readonly IServiceScopeFactory serviceScopeFactory;
    private readonly ParseRunWorkerOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly CancellationTokenSource stopSource = new();
    private readonly CancellationTokenSource leaseLostSource = new();
    private readonly CancellationTokenSource executionSource;
    private readonly Task heartbeatTask;
    private ParseRunLease currentLease;
    private int disposed;
    private int leaseLost;

    internal ParseRunLeaseSession(
        IServiceScopeFactory serviceScopeFactory,
        ParseRunWorkerOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        ParseRunLease currentLease,
        CancellationToken stoppingToken)
    {
        this.serviceScopeFactory = serviceScopeFactory;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.currentLease = currentLease;
        executionSource = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            stopSource.Token,
            leaseLostSource.Token);
        heartbeatTask = RunHeartbeatAsync(executionSource.Token);
    }

    public ParseRunLease CurrentLease => Volatile.Read(ref currentLease);

    public CancellationToken ExecutionCancellationToken => executionSource.Token;

    public bool IsLeaseLost => Volatile.Read(ref leaseLost) != 0;

    public Task<ParseRunLease?> TryStartAsync(
        string initialStage,
        CancellationToken cancellationToken = default)
    {
        return ApplyMutationAsync(
            (services, lease, nowUtc, operationToken) =>
                services.GetRequiredService<IParseRunStateStore>().TryStartAsync(
                    lease,
                    initialStage,
                    nowUtc,
                    operationToken),
            cancellationToken);
    }

    public Task<ParseRunLease?> TryUpdateStageAsync(
        string stage,
        CancellationToken cancellationToken = default)
    {
        return ApplyMutationAsync(
            (services, lease, nowUtc, operationToken) =>
                services.GetRequiredService<IParseRunStateStore>().TryUpdateStageAsync(
                    lease,
                    stage,
                    nowUtc,
                    operationToken),
            cancellationToken);
    }

    public Task<ParseRunLease?> TryRecordProviderSubmissionAsync(
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        return ApplyMutationAsync(
            (services, lease, nowUtc, operationToken) =>
                services.GetRequiredService<IParseRunStateStore>()
                    .TryRecordProviderSubmissionAsync(
                        lease,
                        externalTaskId,
                        nowUtc,
                        operationToken),
            cancellationToken);
    }

    public Task<ParseRunLease?> TrySaveConversionAsync(
        ParseRunConversion conversion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversion);

        return ApplyMutationAsync(
            (services, lease, nowUtc, operationToken) =>
                services.GetRequiredService<IParseRunConversionStore>().TrySaveAsync(
                    lease,
                    conversion,
                    nowUtc,
                    operationToken),
            cancellationToken);
    }

    public Task<ParseRunLease?> TrySaveSubmissionCheckpointAsync(
        ProviderSubmissionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        return ApplyMutationAsync(
            (services, lease, nowUtc, operationToken) =>
                services.GetRequiredService<IParseRunSubmissionCheckpointStore>().TrySaveAsync(
                    lease,
                    checkpoint,
                    nowUtc,
                    operationToken),
            cancellationToken);
    }

    public Task<ParseRunLease?> TryCompleteSubmissionCheckpointAsync(
        ProviderSubmissionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        return ApplyMutationAsync(
            (services, lease, nowUtc, operationToken) =>
                services.GetRequiredService<IParseRunSubmissionCheckpointStore>().TryCompleteAsync(
                    lease,
                    checkpoint,
                    nowUtc,
                    operationToken),
            cancellationToken);
    }

    public async Task<ParseRunExecutionContext?> LoadExecutionContextAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            executionSource.Token,
            cancellationToken);
        await mutationGate.WaitAsync(operationSource.Token);

        try
        {
            if (IsLeaseLost)
            {
                return null;
            }

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IParseRunExecutionContextStore>()
                .LoadAsync(
                    CurrentLease,
                    timeProvider.GetUtcNow().UtcDateTime,
                    operationSource.Token);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<ParseRunFailureTransition?> TryRecordFailureAsync(
        string errorCode,
        string? errorMessage,
        bool retryable,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);
        ThrowIfDisposed();

        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            executionSource.Token,
            cancellationToken);
        await mutationGate.WaitAsync(operationSource.Token);

        try
        {
            if (IsLeaseLost)
            {
                return null;
            }

            var lease = CurrentLease;
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var transition = await scope.ServiceProvider
                .GetRequiredService<IParseRunStateStore>()
                .TryRecordFailureAsync(
                    lease,
                    errorCode,
                    errorMessage,
                    retryable,
                    nowUtc.Add(retryDelay),
                    nowUtc,
                    operationSource.Token);

            if (transition is null)
            {
                MarkLeaseLost(lease);
                return null;
            }

            stopSource.Cancel();
            return transition;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<ParseBundleCommitResult?> TryCommitBundleAsync(
        ParseBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ThrowIfDisposed();

        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            executionSource.Token,
            cancellationToken);
        await mutationGate.WaitAsync(operationSource.Token);

        try
        {
            if (IsLeaseLost)
            {
                return null;
            }

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var renewedLease = await scope.ServiceProvider
                .GetRequiredService<IParseRunLeaseStore>()
                .TryRenewLeaseAsync(
                    CurrentLease,
                    nowUtc,
                    options.LeaseDuration,
                    operationSource.Token);
            if (renewedLease is null)
            {
                MarkLeaseLost(CurrentLease);
                return null;
            }

            Volatile.Write(ref currentLease, renewedLease);
            var result = await scope.ServiceProvider
                .GetRequiredService<IParseBundleCommitStore>()
                .TryCommitAsync(
                    renewedLease,
                    bundle,
                    timeProvider.GetUtcNow().UtcDateTime,
                    operationSource.Token);
            if (result.Status == ParseBundleCommitStatus.LeaseLost)
            {
                MarkLeaseLost(renewedLease);
            }
            else if (result.Status is ParseBundleCommitStatus.Committed
                     or ParseBundleCommitStatus.AlreadyCommitted)
            {
                stopSource.Cancel();
            }

            return result;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await stopSource.CancelAsync();

        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException) when (executionSource.IsCancellationRequested)
        {
        }

        executionSource.Dispose();
        leaseLostSource.Dispose();
        stopSource.Dispose();
        mutationGate.Dispose();
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var lease = CurrentLease;
                var remainingLease = lease.LeaseExpiresAtUtc - timeProvider.GetUtcNow().UtcDateTime;
                if (remainingLease <= TimeSpan.Zero)
                {
                    MarkLeaseLost(lease);
                    return;
                }

                var delay = remainingLease < options.HeartbeatInterval
                    ? remainingLease
                    : options.HeartbeatInterval;
                await Task.Delay(delay, timeProvider, cancellationToken);
                var renewedLease = await ApplyMutationAsync(
                    (services, lease, nowUtc, operationToken) =>
                        services.GetRequiredService<IParseRunLeaseStore>().TryRenewLeaseAsync(
                            lease,
                            nowUtc,
                            options.LeaseDuration,
                            operationToken),
                    cancellationToken);

                if (renewedLease is null)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
            {
                return;
            }
            catch (Exception exception)
            {
                var lease = CurrentLease;
                logger.LogWarning(
                    exception,
                    "Parse Run lease heartbeat failed for {ParseRunId} on worker {WorkerId}.",
                    lease.ParseRunId,
                    lease.WorkerId);

                if (lease.LeaseExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
                {
                    MarkLeaseLost(lease);
                    return;
                }
            }
        }
    }

    private async Task<ParseRunLease?> ApplyMutationAsync(
        Func<IServiceProvider, ParseRunLease, DateTime, CancellationToken, Task<ParseRunLease?>> mutation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            executionSource.Token,
            cancellationToken);
        await mutationGate.WaitAsync(operationSource.Token);

        try
        {
            if (IsLeaseLost)
            {
                return null;
            }

            var lease = CurrentLease;
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var updatedLease = await mutation(
                scope.ServiceProvider,
                lease,
                timeProvider.GetUtcNow().UtcDateTime,
                operationSource.Token);

            if (updatedLease is null)
            {
                MarkLeaseLost(lease);
                return null;
            }

            Volatile.Write(ref currentLease, updatedLease);
            return updatedLease;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private void MarkLeaseLost(ParseRunLease lease)
    {
        if (Interlocked.Exchange(ref leaseLost, 1) != 0)
        {
            return;
        }

        logger.LogWarning(
            "Parse Run lease was lost for {ParseRunId} on worker {WorkerId}.",
            lease.ParseRunId,
            lease.WorkerId);
        leaseLostSource.Cancel();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }
}
