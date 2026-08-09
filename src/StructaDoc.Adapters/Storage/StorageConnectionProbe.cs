using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace StructaDoc.Adapters.Storage;

/// <summary>
/// Why a candidate storage configuration could not be used. The codes are stable tokens the web
/// interface translates; the service does not guess the reader's language.
/// </summary>
public enum StorageProbeCode
{
    /// <summary>The location answered and StructaDoc can write to it.</summary>
    Writable,

    /// <summary>The values do not describe a location this build can open at all.</summary>
    InvalidConfiguration,

    /// <summary>Nothing answered at the address.</summary>
    Unreachable,

    /// <summary>The location answered and refused the credentials.</summary>
    AccessDenied,

    /// <summary>The location answered but the bucket is not there.</summary>
    BucketNotFound,

    /// <summary>The location answered and can be read, but not written to.</summary>
    NotWritable,

    TimedOut,
}

public sealed record StorageProbeResult(StorageProbeCode Code, string Detail)
{
    public bool Succeeded => Code == StorageProbeCode.Writable;
}

/// <summary>
/// Writes and removes one small object at a candidate storage location. Reading is not enough: a
/// bucket that lists but refuses writes accepts every upload attempt and fails each one, and a local
/// path that exists inside a read-only container looks fine until the first document arrives.
///
/// The probe object is written under the configured prefix and deleted again. It is named so that a
/// leftover after a crash is recognisable rather than mistaken for a document.
/// </summary>
public sealed class StorageConnectionProbe
{
    private const string ProbeContent = "structadoc-storage-probe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<StorageProbeResult> ProbeAsync(
        FileStorageOptions candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            candidate.Validate();
        }
        catch (InvalidOperationException error)
        {
            return new StorageProbeResult(StorageProbeCode.InvalidConfiguration, Describe(error));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            return string.Equals(candidate.Provider, "S3", StringComparison.OrdinalIgnoreCase)
                ? await ProbeS3Async(candidate, deadline.Token)
                : ProbeLocal(candidate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StorageProbeResult(StorageProbeCode.TimedOut, string.Empty);
        }
    }

    private static StorageProbeResult ProbeLocal(FileStorageOptions candidate)
    {
        var probePath = string.Empty;
        try
        {
            var stagingPath = Path.Combine(Path.GetFullPath(candidate.RootPath), ".staging");
            Directory.CreateDirectory(stagingPath);
            probePath = Path.Combine(stagingPath, $"probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, ProbeContent);
            return new StorageProbeResult(StorageProbeCode.Writable, string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new StorageProbeResult(
                error is UnauthorizedAccessException ? StorageProbeCode.AccessDenied : StorageProbeCode.NotWritable,
                Describe(error));
        }
        finally
        {
            if (!string.IsNullOrEmpty(probePath) && File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static async Task<StorageProbeResult> ProbeS3Async(
        FileStorageOptions candidate,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(candidate);
        var key = $"{candidate.Prefix.TrimEnd('/')}/.probe/{Guid.NewGuid():N}";

        try
        {
            await client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = candidate.Bucket!,
                    Key = key,
                    ContentBody = ProbeContent,
                },
                cancellationToken);
        }
        catch (AmazonS3Exception error)
        {
            return new StorageProbeResult(MapS3(error), Describe(error, candidate));
        }
        catch (AmazonServiceException error)
        {
            return new StorageProbeResult(StorageProbeCode.Unreachable, Describe(error, candidate));
        }
        catch (HttpRequestException error)
        {
            return new StorageProbeResult(StorageProbeCode.Unreachable, Describe(error, candidate));
        }

        try
        {
            await client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = candidate.Bucket!, Key = key },
                cancellationToken);
        }
        catch (AmazonServiceException)
        {
            // The write is what was being established. A bucket that keeps the probe object is
            // usable, and reporting a failure over a leftover would be misleading.
        }

        return new StorageProbeResult(StorageProbeCode.Writable, string.Empty);
    }

    private static StorageProbeCode MapS3(AmazonS3Exception error) => error.ErrorCode switch
    {
        "NoSuchBucket" => StorageProbeCode.BucketNotFound,
        "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" => StorageProbeCode.AccessDenied,
        _ => error.StatusCode switch
        {
            System.Net.HttpStatusCode.NotFound => StorageProbeCode.BucketNotFound,
            System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized =>
                StorageProbeCode.AccessDenied,
            _ => StorageProbeCode.NotWritable,
        },
    };

    /// <summary>
    /// Built the same way the running service builds its client, so a probe that passes describes
    /// the deployment that would actually run rather than a more forgiving one.
    /// </summary>
    private static AmazonS3Client CreateClient(FileStorageOptions candidate)
    {
        var config = new AmazonS3Config { ForcePathStyle = candidate.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(candidate.ServiceUrl))
        {
            config.ServiceURL = candidate.ServiceUrl;
            config.AuthenticationRegion = candidate.Region ?? "us-east-1";
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(candidate.Region ?? "us-east-1");
        }

        return candidate.AccessKey is null
            ? new AmazonS3Client(config)
            : new AmazonS3Client(
                new BasicAWSCredentials(candidate.AccessKey, candidate.SecretKey),
                config);
    }

    /// <summary>
    /// Bounded, and never a credential. Storage errors quote bucket names, hosts, and status codes,
    /// which is what an administrator needs. A rejected-credential message can also quote the key it
    /// was given, so anything carrying one is dropped rather than repeated to a browser.
    /// </summary>
    private static string Describe(Exception error, FileStorageOptions? candidate = null)
    {
        var message = error.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        if (Quotes(message, candidate?.AccessKey) || Quotes(message, candidate?.SecretKey))
        {
            return string.Empty;
        }

        return message.Length > 200 ? message[..200] : message;
    }

    private static bool Quotes(string message, string? secret) =>
        !string.IsNullOrEmpty(secret)
        && message.Contains(secret, StringComparison.OrdinalIgnoreCase);
}
