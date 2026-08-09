namespace StructaDoc.Adapters.Persistence.Entities;

public sealed class ProviderConfigVersionEntity
{
    public Guid Id { get; set; }
    public Guid ProviderConfigId { get; set; }
    public ProviderConfigEntity ProviderConfig { get; set; } = null!;
    public int VersionNumber { get; set; }
    public required string BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? Backend { get; set; }
    public string? ProtectedCredential { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
