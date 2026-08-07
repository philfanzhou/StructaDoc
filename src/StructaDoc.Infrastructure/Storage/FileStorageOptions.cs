namespace StructaDoc.Infrastructure.Storage;

public sealed class FileStorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; init; } = "Local";

    public string RootPath { get; init; } = "./data/storage";
    public string? ServiceUrl { get; init; }
    public string? Region { get; init; }
    public string? Bucket { get; init; }
    public string Prefix { get; init; } = "structadoc";
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public bool ForcePathStyle { get; init; } = true;

    public void Validate()
    {
        if (!string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Storage provider '{Provider}' is not supported. Supported values: Local, S3.");
        }

        if (string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(RootPath))
        {
            throw new InvalidOperationException("Storage:RootPath must be configured.");
        }
        if (string.Equals(Provider, "S3", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Bucket)) throw new InvalidOperationException("Storage:Bucket must be configured for S3.");
        if ((AccessKey is null) != (SecretKey is null)) throw new InvalidOperationException("Storage:AccessKey and Storage:SecretKey must be configured together.");
        if (Prefix.Contains("..", StringComparison.Ordinal) || Prefix.Contains('\\')) throw new InvalidOperationException("Storage:Prefix must be a safe POSIX key prefix.");
    }
}
