using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace StructaDoc.Platform.Storage;

public sealed class LocalFileStorageHealthCheck(FileStorageOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var probePath = string.Empty;

        try
        {
            var stagingPath = Path.Combine(Path.GetFullPath(options.RootPath), ".staging");
            Directory.CreateDirectory(stagingPath);
            probePath = Path.Combine(stagingPath, $"health-{Guid.NewGuid():N}.tmp");

            await using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);
            await stream.WriteAsync(new byte[] { 0 }, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Local file storage is not writable.",
                exception);
        }
        finally
        {
            if (!string.IsNullOrEmpty(probePath) && File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }
}
