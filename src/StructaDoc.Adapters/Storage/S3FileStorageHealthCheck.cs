using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace StructaDoc.Adapters.Storage;

public sealed class S3FileStorageHealthCheck(IAmazonS3 client, FileStorageOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Bucket!, Prefix = options.Prefix, MaxKeys = 1 }, cancellationToken); return HealthCheckResult.Healthy(); }
        catch (Exception exception) { return HealthCheckResult.Unhealthy("S3-compatible storage is unavailable.", exception); }
    }
}
