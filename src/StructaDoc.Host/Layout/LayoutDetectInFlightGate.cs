namespace StructaDoc.Host.Layout;

/// <summary>
/// The layout endpoint's own in-flight cap: how many layout requests one Host may hold at once.
///
/// Each in-flight request holds up to the image byte bound in request-scoped memory, so this cap —
/// not the runtime's concurrency setting — is what bounds that memory. It is a fixed constant
/// rather than a settings key because it protects the Host's own budget, like the fixed multipart
/// overhead allowance, and it rejects rather than queues: excess requests are answered
/// immediately with <c>429</c> and a <c>Retry-After</c>, and the only queueing on the path is the
/// bounded one inside the adapter's runtime semaphore.
/// </summary>
public sealed class LayoutDetectInFlightGate
{
    /// <summary>
    /// The capacity: at most this many layout requests buffer or execute at once on one Host.
    /// </summary>
    public const int Capacity = 8;

    /// <summary>The delay an overloaded answer suggests before the caller retries.</summary>
    public const int RetryAfterSeconds = 1;

    private readonly SemaphoreSlim slots = new(Capacity, Capacity);

    /// <summary>Takes a slot without waiting. False means the cap is reached.</summary>
    public bool TryEnter() => slots.Wait(0);

    /// <summary>Returns a slot. Every entered slot is released exactly once.</summary>
    public void Release() => slots.Release();
}
