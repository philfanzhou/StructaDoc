namespace StructaDoc.Infrastructure.Storage;

public sealed class FileStorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; init; } = "Local";

    public string RootPath { get; init; } = "./data/storage";

    public void Validate()
    {
        if (!string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Storage provider '{Provider}' is not implemented. Current supported value: Local.");
        }

        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException("Storage:RootPath must be configured.");
        }
    }
}
