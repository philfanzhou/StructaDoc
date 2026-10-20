using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Application.Canonical;

/// <summary>
/// Publishes a Parse Bundle in two phases. Storage verification runs without a lease gate and
/// without an open database transaction, so lease heartbeats keep renewing while result objects
/// are read and hashed. The guarded commit then revalidates ownership with fresh time before any
/// row is written. There is no single-phase path: every publication goes through both phases.
/// </summary>
public interface IParseBundleCommitStore
{
    /// <summary>
    /// Validates a Parse Bundle, reads and hashes every referenced storage object, and returns an
    /// immutable snapshot bound to its fingerprint and its verified object metadata. This phase
    /// creates no database rows and holds no lease, so it can outlast one lease duration while
    /// the Worker's heartbeat keeps the claim alive.
    /// </summary>
    Task<ParseBundleVerificationResult> VerifyBundleAsync(
        ParseBundle bundle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a previously verified bundle in one guarded transaction. The clock is read inside
    /// the transaction and again on every execution-strategy retry, so lease expiry is decided
    /// with current time rather than a timestamp captured before verification. A lost, expired, or
    /// superseded claim yields <see cref="ParseBundleCommitStatus.LeaseLost"/> and writes nothing.
    /// </summary>
    Task<ParseBundleCommitResult> TryCommitAsync(
        ParseRunLease currentLease,
        VerifiedParseBundle verifiedBundle,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The immutable snapshot produced by <see cref="IParseBundleCommitStore.VerifyBundleAsync"/>. The
/// guarded commit accepts this receipt only: the snapshot cannot be swapped for other data, and a
/// snapshot mutated after verification no longer matches its recorded fingerprint and object
/// metadata, so it cannot enter the commit phase.
/// </summary>
public sealed record VerifiedParseBundle(
    ParseBundle Bundle,
    string Fingerprint,
    IReadOnlyDictionary<string, VerifiedStorageObject> VerifiedStorage);

public sealed record VerifiedStorageObject(long SizeBytes, string Sha256);

public enum ParseBundleVerificationStatus
{
    Verified,
    AlreadyCommitted,
    Conflict,
    InvalidBundle,
    StorageMismatch,
}

public sealed record ParseBundleVerificationResult(
    ParseBundleVerificationStatus Status,
    VerifiedParseBundle? VerifiedBundle = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public enum ParseBundleCommitStatus
{
    Committed,
    AlreadyCommitted,
    LeaseLost,
    InvalidBundle,
    StorageMismatch,
    Conflict,
}

public sealed record ParseBundleCommitResult(
    ParseBundleCommitStatus Status,
    string? ErrorCode = null,
    string? ErrorMessage = null);
