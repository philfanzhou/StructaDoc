using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using StructaDoc.Application.Conversion;

namespace StructaDoc.Adapters.Conversion;

public sealed class LibreOfficeProcessRunner : ILibreOfficeProcessRunner
{
    private const int CaptureLimitBytes = 16 * 1024;
    private const int BufferSize = 4096;

    public async Task<LibreOfficeProcessResult> RunAsync(
        LibreOfficeProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Timeout, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(
            request.ResourceInspectionInterval,
            TimeSpan.FromMilliseconds(50));
        if (request.MaxWorkingDirectoryBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.MaxOutputDirectoryBytes is <= 0
            || (request.MaxOutputDirectoryBytes.HasValue
                && string.IsNullOrWhiteSpace(request.OutputDirectory)))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw ConfigurationFailure();
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw ConfigurationFailure(exception);
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream);
        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var executionSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            while (!process.HasExited)
            {
                await Task.Delay(request.ResourceInspectionInterval, executionSource.Token);
                if (request.MaxWorkingDirectoryBytes.HasValue
                    && GetDirectorySize(request.WorkingDirectory)
                        > request.MaxWorkingDirectoryBytes.Value)
                {
                    Kill(process);
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw new DocumentConversionException(
                        "document-conversion-temporary-limit-exceeded",
                        "The document conversion exceeded its temporary disk limit.");
                }

                if (request.MaxOutputDirectoryBytes.HasValue
                    && request.OutputDirectory is not null
                    && GetDirectorySize(request.OutputDirectory)
                        > request.MaxOutputDirectoryBytes.Value)
                {
                    Kill(process);
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw new DocumentConversionException(
                        "document-conversion-output-size-invalid",
                        "The converted PDF exceeds the output size limit.");
                }
            }

            await process.WaitForExitAsync(executionSource.Token);
        }
        catch (OperationCanceledException) when (executionSource.IsCancellationRequested)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new DocumentConversionException(
                "document-conversion-timeout",
                "The document conversion exceeded its execution time limit.");
        }

        return new LibreOfficeProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static async Task<string> ReadBoundedAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var capture = new MemoryStream(CaptureLimitBytes);
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer);
                if (bytesRead == 0)
                {
                    break;
                }

                var remaining = CaptureLimitBytes - checked((int)capture.Length);
                if (remaining > 0)
                {
                    capture.Write(buffer, 0, Math.Min(remaining, bytesRead));
                }
            }

            return Encoding.UTF8.GetString(capture.GetBuffer(), 0, checked((int)capture.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long GetDirectorySize(string path)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(file).Length);
        }

        return total;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static DocumentConversionException ConfigurationFailure(Exception? innerException = null) =>
        new(
            "document-converter-unavailable",
            "The configured LibreOffice executable could not be started.",
            retryable: false,
            innerException);
}
