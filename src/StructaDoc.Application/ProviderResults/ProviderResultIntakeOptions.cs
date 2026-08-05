namespace StructaDoc.Application.ProviderResults;

public sealed class ProviderResultIntakeOptions
{
    public const string SectionName = "ProviderResults";

    public long MaxArchiveBytes { get; init; } = 512L * 1024 * 1024;

    public int MaxEntryCount { get; init; } = 20_000;

    public long MaxEntryBytes { get; init; } = 256L * 1024 * 1024;

    public long MaxExpandedBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public double MaxCompressionRatio { get; init; } = 200;

    public int MaxEntryPathBytes { get; init; } = 2048;

    public long MaxCentralDirectoryBytes { get; init; } = 64L * 1024 * 1024;

    public string TemporaryPath { get; init; } = Path.Combine(
        Path.GetTempPath(),
        "structadoc-provider-results");

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCentralDirectoryBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxExpandedBytes, MaxEntryBytes);

        if (!double.IsFinite(MaxCompressionRatio) || MaxCompressionRatio < 1)
        {
            throw new InvalidOperationException(
                "ProviderResults:MaxCompressionRatio must be a finite value of at least one.");
        }

        if (MaxEntryPathBytes is < 64 or > 16_384)
        {
            throw new InvalidOperationException(
                "ProviderResults:MaxEntryPathBytes must be between 64 and 16384.");
        }

        if (string.IsNullOrWhiteSpace(TemporaryPath))
        {
            throw new InvalidOperationException(
                "ProviderResults:TemporaryPath must be configured.");
        }
    }
}
