namespace StructaDoc.Contracts.Resources;

// Deletion is accepted rather than performed: the resource is marked pending at once and a Cleanup
// Job removes its stored objects afterwards, so the caller is handed the job to follow.
public sealed record ResourceDeletionResponse(Guid? CleanupJobId, string Status);
