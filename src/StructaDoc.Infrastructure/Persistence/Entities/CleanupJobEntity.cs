namespace StructaDoc.Infrastructure.Persistence.Entities;

public sealed class CleanupJobEntity
{
    public Guid Id { get; set; }
    public required string TargetType { get; set; }
    public Guid TargetId { get; set; }
    public required string StorageRefsJson { get; set; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public long ConcurrencyVersion { get; set; }
}
