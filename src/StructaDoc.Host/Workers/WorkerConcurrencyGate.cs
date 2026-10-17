namespace StructaDoc.Host.Workers;

/// <summary>
/// Bounds how many Parse Run executions this Host runs at the same time, with a ceiling that can
/// change while executions are in flight — which is what makes <c>Worker:MaxConcurrency</c> a
/// running adjustment rather than a startup one.
///
/// The permit the dispatch loop waits for is taken before a claim and released when the execution
/// finishes, so lowering the ceiling does not touch runs already executing (per the lease model, a
/// started attempt runs to its own outcome); it only stops new claims until the running count falls
/// below the new ceiling. Raising it admits waiting dispatches at once.
/// </summary>
public sealed class WorkerConcurrencyGate
{
    private readonly object sync = new();
    private readonly Queue<Waiter> waiters = new();
    private int limit;
    private int active;

    public WorkerConcurrencyGate(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "The concurrency limit must be at least one.");
        }

        this.limit = limit;
    }

    /// <summary>The ceiling in force right now, initialized from what startup bound.</summary>
    public int Limit
    {
        get
        {
            lock (sync)
            {
                return limit;
            }
        }
    }

    /// <summary>
    /// Replaces the ceiling. Returns <see langword="false"/> rather than throwing when the value is
    /// not one a running service can use, because the caller still has a setting write to answer:
    /// the previous ceiling stays in force and the change reports itself as needing a restart.
    /// </summary>
    public bool TrySetLimit(int value)
    {
        if (value < 1)
        {
            return false;
        }

        lock (sync)
        {
            limit = value;
            AdmitWaiters();
            return true;
        }
    }

    /// <summary>
    /// Resolves when a execution slot is free under the current ceiling. Waiters are admitted in
    /// the order they arrived, so a lowered ceiling releases capacity to whoever waited first.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var waiter = new Waiter();
        waiter.Registration = cancellationToken.Register(
            static state => ((Waiter)state!).Cancel(),
            waiter);

        bool admitted = false;
        lock (sync)
        {
            // A token that cancelled during registration has already completed the waiter; handing
            // out a permit for it would leak one nothing will ever release.
            if (waiter.Completion.Task.IsCanceled)
            {
                waiter.Registration.Dispose();
                return waiter.Completion.Task;
            }

            if (active < limit)
            {
                admitted = waiter.Completion.TrySetResult();
                if (admitted)
                {
                    active++;
                }
            }
            else
            {
                waiters.Enqueue(waiter);
            }
        }

        if (admitted)
        {
            waiter.Registration.Dispose();
        }

        return waiter.Completion.Task;
    }

    /// <summary>Returns one execution slot, handing it straight to the longest waiting dispatch.</summary>
    public void Release()
    {
        lock (sync)
        {
            if (active == 0)
            {
                return;
            }

            active--;
            AdmitWaiters();
        }
    }

    // The caller holds the lock. A waiter completed by its own cancellation token is skipped
    // without a permit, because its dispatch already observed the cancellation.
    private void AdmitWaiters()
    {
        while (active < limit && waiters.Count > 0)
        {
            var waiter = waiters.Dequeue();
            waiter.Registration.Dispose();
            if (waiter.Completion.TrySetResult())
            {
                active++;
            }
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }

        public void Cancel() => Completion.TrySetCanceled();
    }
}
