namespace StructaDoc.Application.ProviderResults;

public sealed class ProviderResultNormalizationOptions
{
    public const string SectionName = "ProviderResultNormalization";

    public long MaxMarkdownBytes { get; init; } = 64L * 1024 * 1024;

    public long MaxJsonBytes { get; init; } = 64L * 1024 * 1024;

    public long MaxAssetBytes { get; init; } = 256L * 1024 * 1024;

    public string TemporaryPath { get; init; } = Path.Combine(
        Path.GetTempPath(),
        "structadoc-provider-normalization");

    public void Validate()
    {
        if (MaxMarkdownBytes <= 0
            || MaxJsonBytes <= 0
            || MaxAssetBytes <= 0
            || MaxMarkdownBytes > int.MaxValue
            || MaxJsonBytes > int.MaxValue)
        {
            throw new InvalidOperationException(
                "Provider result normalization limits must be positive, and text limits cannot exceed Int32.MaxValue.");
        }

        if (string.IsNullOrWhiteSpace(TemporaryPath))
        {
            throw new InvalidOperationException(
                "Provider result normalization requires a temporary path.");
        }
    }
}
