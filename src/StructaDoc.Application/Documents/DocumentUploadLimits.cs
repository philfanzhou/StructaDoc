namespace StructaDoc.Application.Documents;

/// <summary>
/// The upload size limit as the running service uses it, rather than as
/// <see cref="DocumentIngestionOptions"/> happened to be bound at startup. Options are a startup
/// snapshot, so a limit an administrator changes without a restart reaches the running service
/// through this holder instead: the storage write during ingestion — the authoritative rejection —
/// and the endpoint's request-size bound both read the value it holds at the moment they run.
/// </summary>
public sealed class DocumentUploadLimits
{
    private long maxUploadBytes;

    public DocumentUploadLimits(long maxUploadBytes)
    {
        if (maxUploadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxUploadBytes),
                "The upload size limit must be positive.");
        }

        this.maxUploadBytes = maxUploadBytes;
    }

    public long MaxUploadBytes => Volatile.Read(ref maxUploadBytes);

    /// <summary>
    /// Replaces the limit in force. Returns <see langword="false"/> rather than throwing when the
    /// value is not one a running service can use, because the caller still has a write to answer:
    /// the previous limit stays in force and the change reports itself as needing a restart.
    /// </summary>
    public bool TrySet(long value)
    {
        if (value <= 0)
        {
            return false;
        }

        Volatile.Write(ref maxUploadBytes, value);
        return true;
    }
}
