using StructaDoc.Application.ParseRuns;

namespace StructaDoc.Application.Canonical;

public interface IParseBundleCommitStore
{
    Task<ParseBundleCommitResult> TryCommitAsync(
        ParseRunLease currentLease,
        ParseBundle bundle,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

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
