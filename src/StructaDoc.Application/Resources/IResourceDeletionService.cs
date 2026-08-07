using StructaDoc.Application.Authentication;

namespace StructaDoc.Application.Resources;

public interface IResourceDeletionService
{
    Task<ResourceDeletionResult> RequestDocumentDeletionAsync(Guid documentId, ResourceAccessContext access, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<ResourceDeletionResult> RequestParseRunDeletionAsync(Guid parseRunId, ResourceAccessContext access, DateTime nowUtc, CancellationToken cancellationToken = default);
}

public enum ResourceDeletionStatus { Accepted, AlreadyPending, NotFound, ActiveParseRuns }

public sealed record ResourceDeletionResult(ResourceDeletionStatus Status, Guid? CleanupJobId = null);
