namespace StructaDoc.Platform.Conversion;

public sealed class LibreOfficeConversionOptions
{
    public const string SectionName = "LibreOffice";

    public bool Enabled { get; init; } = true;

    public string ExecutablePath { get; init; } = "libreoffice";

    public string TemporaryPath { get; init; } = "./data/temp/libreoffice";

    public int MaxConcurrency { get; init; } = 1;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan ResourceInspectionInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public long MaxInputBytes { get; init; } = 100L * 1024 * 1024;

    public long MaxOutputBytes { get; init; } = 200L * 1024 * 1024;

    public long MaxTemporaryBytes { get; init; } = 512L * 1024 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            throw new InvalidOperationException("LibreOffice:ExecutablePath must be configured.");
        }

        if (string.IsNullOrWhiteSpace(TemporaryPath))
        {
            throw new InvalidOperationException("LibreOffice:TemporaryPath must be configured.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrency);
        ArgumentOutOfRangeException.ThrowIfLessThan(Timeout, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(
            ResourceInspectionInterval,
            TimeSpan.FromMilliseconds(50));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            ResourceInspectionInterval,
            TimeSpan.FromSeconds(5));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTemporaryBytes);

        if (MaxTemporaryBytes < Math.Max(MaxInputBytes, MaxOutputBytes))
        {
            throw new InvalidOperationException(
                "LibreOffice:MaxTemporaryBytes must cover the configured input and output limits.");
        }
    }
}
