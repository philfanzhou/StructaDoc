namespace StructaDoc.Host.Recognition;

/// <summary>
/// The recognition endpoint's own in-flight cap: how many recognition requests one Host may hold
/// at once. It has the layout gate's shape and size but is a separate gate, so recognition traffic
/// can never make the layout route answer <c>429</c>: the layout mode's behavior is unchanged by
/// turning recognition on. It rejects rather than queues; the only queueing on the path is the
/// bounded one inside the shared cloud exchange bound.
/// </summary>
public sealed class ImageRecognizeInFlightGate
{
    /// <summary>The capacity: at most this many recognition requests buffer or execute at once on one Host.</summary>
    public const int Capacity = 8;

    /// <summary>The delay an overloaded answer suggests before the caller retries.</summary>
    public const int RetryAfterSeconds = 1;

    private readonly SemaphoreSlim slots = new(Capacity, Capacity);

    /// <summary>Takes a slot without waiting. False means the cap is reached.</summary>
    public bool TryEnter() => slots.Wait(0);

    /// <summary>Returns a slot. Every entered slot is released exactly once.</summary>
    public void Release() => slots.Release();
}
