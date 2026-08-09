namespace StructaDoc.Platform.Conversion;

public interface ILibreOfficeProcessRunner
{
    Task<LibreOfficeProcessResult> RunAsync(
        LibreOfficeProcessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record LibreOfficeProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    TimeSpan ResourceInspectionInterval,
    long? MaxWorkingDirectoryBytes = null,
    string? OutputDirectory = null,
    long? MaxOutputDirectoryBytes = null);

public sealed record LibreOfficeProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
