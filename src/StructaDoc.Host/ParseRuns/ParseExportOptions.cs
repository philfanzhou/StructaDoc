namespace StructaDoc.Host.ParseRuns;

/// <summary>
/// Deployment-owned settings for packaged exports. The ZIP export builds its archive on disk
/// before returning it, so the directory it stages in must be writable space the deployment
/// sized for results — not the bounded system temporary directory a supported container mounts
/// smaller than one permitted result.
/// </summary>
public sealed class ParseExportOptions
{
    public const string SectionName = "ParseExports";

    public string TemporaryPath { get; init; } = "./data/temp/exports";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TemporaryPath))
        {
            throw new InvalidOperationException(
                "ParseExports:TemporaryPath must be configured.");
        }
    }
}
