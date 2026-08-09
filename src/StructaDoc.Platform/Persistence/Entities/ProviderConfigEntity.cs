namespace StructaDoc.Platform.Persistence.Entities;

public sealed class ProviderConfigEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ProviderType { get; set; }
    public bool IsEnabled { get; set; }
    public string? DefaultMarker { get; set; }
    public Guid CurrentVersionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public long ConcurrencyVersion { get; set; }
    public ICollection<ProviderConfigVersionEntity> Versions { get; } = [];
}
