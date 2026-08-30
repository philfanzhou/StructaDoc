namespace StructaDoc.Adapters.Persistence.Entities;

public sealed class ParseRunEntity
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public DocumentEntity Document { get; set; } = null!;

    public required string Status { get; set; }

    public string? Stage { get; set; }

    public required string ProviderType { get; set; }

    public Guid ProviderConfigId { get; set; }

    public Guid ProviderConfigVersion { get; set; }

    public required string OptionsJson { get; set; }

    public required string SourceMediaType { get; set; }

    public required string SubmittedMediaType { get; set; }

    public string? ConversionJson { get; set; }

    public string? ExternalTaskId { get; set; }

    public string? ProtectedSubmissionContinuation { get; set; }

    public string? ResultSchemaVersion { get; set; }

    public string? ResultSha256 { get; set; }

    public string? ProviderMetadataJson { get; set; }

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; }

    public DateTime NextAttemptAtUtc { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public byte[]? CreatedByIssuer { get; set; }

    public byte[]? CreatedBySubject { get; set; }

    public byte[]? CreatedByLegacy { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? ClaimedBy { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public long ConcurrencyVersion { get; set; }

    public string LifecycleState { get; set; } = "active";

    public DateTime? DeletionRequestedAtUtc { get; set; }

    public ICollection<ParsePageEntity> Pages { get; } = [];

    public ICollection<ParseBlockEntity> Blocks { get; } = [];

    public ICollection<ParseAssetEntity> Assets { get; } = [];

    public ICollection<ParseArtifactEntity> Artifacts { get; } = [];

    public ICollection<ParseSegmentEntity> Segments { get; } = [];
}
