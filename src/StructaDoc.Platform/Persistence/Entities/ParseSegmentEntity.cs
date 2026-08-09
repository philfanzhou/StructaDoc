namespace StructaDoc.Platform.Persistence.Entities;

public sealed class ParseSegmentEntity
{
    public Guid Id { get; set; }
    public Guid ParseRunId { get; set; }
    public ParseRunEntity ParseRun { get; set; } = null!;
    public int Index { get; set; }
    public int StartPage { get; set; }
    public int EndPage { get; set; }
    public required string StorageRef { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public required string Status { get; set; }
    public string? ExternalTaskId { get; set; }
    public string? ProtectedSubmissionContinuation { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
