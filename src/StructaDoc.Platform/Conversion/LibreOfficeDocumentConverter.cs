using System.Buffers;
using StructaDoc.Application.Conversion;

namespace StructaDoc.Platform.Conversion;

public sealed class LibreOfficeDocumentConverter : IDocumentConverter, IDisposable
{
    public const string ConverterType = "libreoffice";

    private const string PdfMediaType = DocumentConversionMediaTypes.Pdf;
    private const int BufferSize = 64 * 1024;
    private static readonly IReadOnlyDictionary<string, string> SourceExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/msword"] = ".doc",
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
            ["application/vnd.ms-excel"] = ".xls",
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
            ["application/vnd.ms-powerpoint"] = ".ppt",
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        };

    private readonly LibreOfficeConversionOptions options;
    private readonly ILibreOfficeProcessRunner processRunner;
    private readonly SemaphoreSlim concurrencyGate;
    private readonly SemaphoreSlim versionGate = new(1, 1);
    private readonly string temporaryRoot;
    private string? converterVersion;
    private int disposed;

    public LibreOfficeDocumentConverter(
        LibreOfficeConversionOptions options,
        ILibreOfficeProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processRunner);
        options.Validate();

        this.options = options;
        this.processRunner = processRunner;
        concurrencyGate = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        temporaryRoot = Path.GetFullPath(options.TemporaryPath);
        Directory.CreateDirectory(temporaryRoot);
    }

    public bool Supports(string sourceMediaType, string outputMediaType) =>
        options.Enabled
        && SourceExtensions.ContainsKey(NormalizeMediaType(sourceMediaType))
        && string.Equals(
            NormalizeMediaType(outputMediaType),
            PdfMediaType,
            StringComparison.OrdinalIgnoreCase);

    public async Task<DocumentConversionResult> ConvertAsync(
        DocumentConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        var sourceMediaType = NormalizeMediaType(request.SourceMediaType);
        var outputMediaType = NormalizeMediaType(request.OutputMediaType);
        if (!Supports(sourceMediaType, outputMediaType))
        {
            throw new DocumentConversionException(
                "document-conversion-unsupported",
                "The requested document conversion is not supported.");
        }

        if (request.SourceSizeBytes > options.MaxInputBytes)
        {
            throw new DocumentConversionException(
                "document-conversion-input-too-large",
                "The source document exceeds the conversion input size limit.");
        }

        await concurrencyGate.WaitAsync(cancellationToken);
        var ownsGate = true;
        var workingDirectory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));

        try
        {
            var outputDirectory = Path.Combine(workingDirectory, "output");
            var profileDirectory = Path.Combine(workingDirectory, "profile");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(profileDirectory);
            var inputPath = Path.Combine(
                workingDirectory,
                $"source{SourceExtensions[sourceMediaType]}");
            await WriteInputAsync(request, inputPath, cancellationToken);

            var version = await GetConverterVersionAsync(workingDirectory, cancellationToken);
            var profileUri = new Uri(
                Path.EndsInDirectorySeparator(profileDirectory)
                    ? profileDirectory
                    : profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri;
            var processResult = await processRunner.RunAsync(
                new LibreOfficeProcessRequest(
                    options.ExecutablePath,
                    [
                        "--headless",
                        "--nologo",
                        "--nodefault",
                        "--nolockcheck",
                        "--norestore",
                        $"-env:UserInstallation={profileUri}",
                        "--convert-to",
                        "pdf",
                        "--outdir",
                        outputDirectory,
                        inputPath,
                    ],
                    workingDirectory,
                    options.Timeout,
                    options.ResourceInspectionInterval,
                    options.MaxTemporaryBytes,
                    outputDirectory,
                    options.MaxOutputBytes),
                cancellationToken);
            if (processResult.ExitCode != 0)
            {
                throw new DocumentConversionException(
                    "document-conversion-failed",
                    "LibreOffice did not complete the document conversion successfully.");
            }

            var outputPath = Path.Combine(outputDirectory, "source.pdf");
            var output = await OpenAndValidateOutputAsync(outputPath, cancellationToken);
            var result = new DocumentConversionResult(
                ConverterType,
                version,
                PdfMediaType,
                output.Length,
                output,
                () =>
                {
                    TryDeleteWorkingDirectory(workingDirectory);
                    concurrencyGate.Release();
                    return ValueTask.CompletedTask;
                });
            ownsGate = false;
            return result;
        }
        catch
        {
            TryDeleteWorkingDirectory(workingDirectory);
            throw;
        }
        finally
        {
            if (ownsGate)
            {
                concurrencyGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            concurrencyGate.Dispose();
            versionGate.Dispose();
        }
    }

    private async Task WriteInputAsync(
        DocumentConversionRequest request,
        string inputPath,
        CancellationToken cancellationToken)
    {
        await using var source = await request.OpenReadAsync(cancellationToken);
        if (!source.CanRead)
        {
            throw new DocumentConversionException(
                "document-conversion-source-unreadable",
                "The source document cannot be read for conversion.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long sizeBytes = 0;
            await using var destination = new FileStream(
                inputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                sizeBytes = checked(sizeBytes + bytesRead);
                if (sizeBytes > options.MaxInputBytes
                    || sizeBytes > request.SourceSizeBytes)
                {
                    throw new DocumentConversionException(
                        "document-conversion-input-size-mismatch",
                        "The source document size does not match its stored metadata.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            if (sizeBytes != request.SourceSizeBytes)
            {
                throw new DocumentConversionException(
                    "document-conversion-input-size-mismatch",
                    "The source document size does not match its stored metadata.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> GetConverterVersionAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref converterVersion) is { } cached)
        {
            return cached;
        }

        await versionGate.WaitAsync(cancellationToken);
        try
        {
            if (converterVersion is not null)
            {
                return converterVersion;
            }

            var result = await processRunner.RunAsync(
                new LibreOfficeProcessRequest(
                    options.ExecutablePath,
                    ["--headless", "--version"],
                    workingDirectory,
                    TimeSpan.FromSeconds(Math.Min(30, options.Timeout.TotalSeconds)),
                    options.ResourceInspectionInterval),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new DocumentConversionException(
                    "document-converter-version-unavailable",
                    "The LibreOffice version could not be determined.");
            }

            converterVersion = ParseVersion(result.StandardOutput);
            return converterVersion;
        }
        finally
        {
            versionGate.Release();
        }
    }

    private async Task<FileStream> OpenAndValidateOutputAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath))
        {
            throw new DocumentConversionException(
                "document-conversion-output-missing",
                "LibreOffice did not produce the expected PDF output.");
        }

        var fileInfo = new FileInfo(outputPath);
        if (fileInfo.Length <= 0 || fileInfo.Length > options.MaxOutputBytes)
        {
            throw new DocumentConversionException(
                "document-conversion-output-size-invalid",
                "The converted PDF is empty or exceeds the output size limit.");
        }

        var stream = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var signature = new byte[5];
            var bytesRead = await stream.ReadAsync(signature, cancellationToken);
            if (bytesRead != signature.Length
                || !signature.AsSpan().SequenceEqual("%PDF-"u8))
            {
                throw new DocumentConversionException(
                    "document-conversion-output-invalid",
                    "LibreOffice produced an invalid PDF output.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static string ParseVersion(string output)
    {
        var firstLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine)
            || firstLine.Length > 128
            || firstLine.Any(char.IsControl)
            || !firstLine.StartsWith("LibreOffice ", StringComparison.OrdinalIgnoreCase))
        {
            throw new DocumentConversionException(
                "document-converter-version-invalid",
                "LibreOffice returned an invalid version description.");
        }

        return firstLine;
    }

    private static string NormalizeMediaType(string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        var separator = mediaType.IndexOf(';', StringComparison.Ordinal);
        return (separator < 0 ? mediaType : mediaType[..separator]).Trim().ToLowerInvariant();
    }

    private static void TryDeleteWorkingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
