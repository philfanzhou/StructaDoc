using System.IO.Compression;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

/// <summary>
/// The one bound on in-flight MinerU Cloud image exchanges on a Host, shared by every
/// non-archiving mode that uses the cloud (layout detection per ADR-0014, image text recognition
/// per ADR-0015).
///
/// It is a singleton rather than a field of the adapter because the adapters are request-scoped —
/// their configuration source reads the request's business database context — and a bound held per
/// instance would bound nothing. It is fixed rather than settings-managed because the account
/// behind the credential is shared with document parsing: four in-flight image exchanges across
/// both modes leave the account room for Parse Runs.
/// </summary>
public sealed class MinerUCloudExchangeLimiter
{
    public const int MaxConcurrency = 4;

    private readonly SemaphoreSlim slots = new(MaxConcurrency, MaxConcurrency);

    internal Task WaitAsync(CancellationToken cancellationToken) => slots.WaitAsync(cancellationToken);

    internal void Release() => slots.Release();
}

/// <summary>
/// What distinguishes one non-archiving mode's use of the cloud exchange: its name (which prefixes
/// every operation, error code, and message, so the layout mode's stable codes stay exactly as
/// they were), the unsupported-capability code it reports, and the submission switches it asks for.
/// </summary>
internal sealed record MinerUCloudExchangeMode(
    string Name,
    string UnsupportedCapabilityErrorCode,
    string Remedy,
    bool IsOcr,
    bool EnableTable,
    bool EnableFormula)
{
    public string TimeoutErrorCode => $"mineru-{Name}-timeout";

    public string Operation(string leg) => $"{Name}-cloud-{leg}";
}

/// <summary>
/// The non-archiving MinerU Cloud image exchange (ADR-0014 point 3, reused by ADR-0015): the
/// official signed batch upload, polling, and result download protocol the full-parsing cloud
/// adapter uses, run for one page image inside one request.
///
/// Nothing about the exchange persists: the configuration is a per-request snapshot, the cloud
/// batch identifier lives in request memory only, and the downloaded archive is one bounded
/// in-memory buffer disposed with the request. One overall deadline covers submission, upload,
/// polling, download, and the caller's reduction; an expired deadline is the mode's stable timeout
/// and the caller's own cancellation propagates.
/// </summary>
internal sealed class MinerUCloudImageExchange
{
    /// <summary>The deadline bound across the whole exchange that the shipped defaults describe.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The bound on the downloaded result archive. One page image cannot legitimately approach it;
    /// it exists so a hostile or broken result endpoint cannot describe an unbounded download.
    /// </summary>
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;

    /// <summary>The bound on the single layout JSON entry read out of that archive.</summary>
    public const long MaximumLayoutJsonBytes = 16L * 1024 * 1024;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far the cloud page proportions may drift from the submitted image's orientation-applied
    /// proportions before the output counts as incompatible. The observed cloud output scales the
    /// page proportionally (a 1240x1754 image answered with a 446x631 page), so a drift past this
    /// is a different frame rather than rounding.
    /// </summary>
    private const double MaximumAspectDrift = 0.05;

    private readonly HttpClient providerApiClient;
    private readonly HttpClient signedTransferClient;
    private readonly ILayoutCloudConfigurationSource configurationSource;
    private readonly MinerUCloudExchangeLimiter limiter;
    private readonly TimeSpan deadline;
    private readonly MinerUCloudExchangeMode mode;

    public MinerUCloudImageExchange(
        HttpClient providerApiClient,
        HttpClient signedTransferClient,
        ILayoutCloudConfigurationSource configurationSource,
        MinerUCloudExchangeLimiter limiter,
        TimeSpan? deadline,
        MinerUCloudExchangeMode mode)
    {
        ArgumentNullException.ThrowIfNull(providerApiClient);
        ArgumentNullException.ThrowIfNull(signedTransferClient);
        ArgumentNullException.ThrowIfNull(configurationSource);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(mode);
        var bound = deadline ?? DefaultDeadline;
        ArgumentOutOfRangeException.ThrowIfLessThan(bound, TimeSpan.FromMilliseconds(1));
        this.providerApiClient = providerApiClient;
        this.signedTransferClient = signedTransferClient;
        this.configurationSource = configurationSource;
        this.limiter = limiter;
        this.deadline = bound;
        this.mode = mode;
    }

    /// <summary>
    /// Runs the exchange for <paramref name="request"/> and hands the validated page of the cloud's
    /// layout JSON to <paramref name="reduce"/>, inside the same deadline.
    /// </summary>
    public async Task<TResult> RunAsync<TResult>(
        LayoutDetectionRequest request,
        Func<MinerUCloudLayoutPage, ProviderExecutionConfiguration, TResult> reduce,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reduce);
        cancellationToken.ThrowIfCancellationRequested();

        // The configuration is a per-request snapshot of the default Provider configuration — the
        // same shape the Parse Run execution context builds, held for this request only and never
        // recorded anywhere. A deployment state that cannot serve the exchange is the explicit
        // unsupported-capability configuration error.
        var snapshot = await configurationSource.LoadAsync(cancellationToken);
        if (snapshot.State != LayoutCloudConfigurationState.Ready
            || snapshot.Configuration is not { } cloud)
        {
            throw new ProviderException(
                mode.UnsupportedCapabilityErrorCode,
                $"The MinerU Cloud {mode.Name} backend is unavailable: {snapshot.Describe(mode.Name)}. "
                    + mode.Remedy,
                ProviderFailureCategory.Configuration);
        }

        MinerUHttpProtocol.ValidateConfiguration(cloud, ProviderTypes.MinerUCloud, requireHttps: true);
        if (request.StoredWidth is null || request.StoredHeight is null)
        {
            // The cloud route reports geometry against the original image, and only the caller's
            // inspection knows the original's stored dimensions. Reaching here is a wiring defect,
            // not a deployment state a message can repair.
            throw new ArgumentException(
                $"The MinerU Cloud {mode.Name} backend requires the request's inspected image dimensions.",
                nameof(request));
        }

        // The bound is held for the whole exchange, so a slow cloud exchange cannot accumulate
        // unlimited in-flight work no matter how many callers arrive.
        await limiter.WaitAsync(cancellationToken);
        try
        {
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchange.CancelAfter(deadline);
            try
            {
                var (batchId, uploadUri) = await SubmitBatchAsync(cloud, request, exchange.Token);
                var resultUri = await DriveToCompletionAsync(
                    cloud,
                    request,
                    batchId,
                    uploadUri,
                    exchange.Token);
                using var archive = await DownloadArchiveAsync(resultUri, exchange.Token);
                using var page = ReadPage(archive, request);
                exchange.Token.ThrowIfCancellationRequested();
                return reduce(page, cloud);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderException(
                    mode.TimeoutErrorCode,
                    $"The MinerU Cloud {mode.Name} exchange exceeded its overall deadline.",
                    ProviderFailureCategory.Transient);
            }
        }
        finally
        {
            limiter.Release();
        }
    }

    /// <summary>
    /// Allocates the cloud batch and its single signed upload URL. The batch identifier is random
    /// per request — there is no Parse Run to name it after — and lives in request memory only.
    /// </summary>
    private async Task<(string BatchId, Uri UploadUri)> SubmitBatchAsync(
        ProviderExecutionConfiguration cloud,
        LayoutDetectionRequest request,
        CancellationToken exchangeToken)
    {
        var operation = mode.Operation("submit");

        // The switches are a request, never an advertisement: the cloud service decides what its
        // pipeline actually runs (the observed answer executed recognition regardless of them).
        var file = new Dictionary<string, object>
        {
            ["name"] = FileNameFor(request.MediaType),
            ["data_id"] = Guid.NewGuid().ToString("N"),
            ["is_ocr"] = mode.IsOcr,
        };
        var payload = new Dictionary<string, object>
        {
            ["files"] = new[] { file },
            ["model_version"] = cloud.Model ?? ProviderTypeDescriptors.MinerUCloudDefaultModel,
            ["enable_formula"] = mode.EnableFormula,
            ["enable_table"] = mode.EnableTable,
            ["language"] = "ch",
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            MinerUHttpProtocol.BuildEndpoint(cloud.BaseUri, "api/v4/file-urls/batch"))
        {
            Content = MinerUHttpProtocol.CreateJsonContent(payload),
        };
        MinerUHttpProtocol.AddBearerCredential(message, cloud.Credential, required: true);

        using var response = await MinerUHttpProtocol.SendAsync(
            providerApiClient,
            message,
            HttpCompletionOption.ResponseHeadersRead,
            operation,
            exchangeToken);
        MinerUHttpProtocol.EnsureSuccess(response, operation);
        using var body = await MinerUHttpProtocol.ReadJsonAsync(response, operation, exchangeToken);
        var data = MinerUHttpProtocol.ReadSuccessfulData(body.RootElement, operation);
        var batchId = MinerUHttpProtocol.ReadRequiredCloudString(data, "batch_id", operation);
        MinerUHttpProtocol.ValidateExternalTaskId(batchId);
        if (!data.TryGetProperty("file_urls", out var fileUrls)
            || fileUrls.ValueKind != JsonValueKind.Array
            || fileUrls.GetArrayLength() != 1
            || fileUrls[0].ValueKind != JsonValueKind.String)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                operation,
                $"The MinerU Cloud {mode.Name} submission response did not contain one upload URL.");
        }

        var uploadUri = MinerUHttpProtocol.ValidateSignedUri(
            fileUrls[0].GetString(),
            mode.Operation("upload"));
        return (batchId, uploadUri);
    }

    /// <summary>
    /// Uploads while the cloud still waits for the file, then polls within the overall deadline
    /// until the extraction finishes and hands back the signed result URL.
    /// </summary>
    private async Task<Uri> DriveToCompletionAsync(
        ProviderExecutionConfiguration cloud,
        LayoutDetectionRequest request,
        string batchId,
        Uri uploadUri,
        CancellationToken exchangeToken)
    {
        while (true)
        {
            var (state, fullZipUrl) = await GetBatchStateAsync(cloud, batchId, exchangeToken);
            switch (state)
            {
                case "waiting-file":
                    await UploadAsync(request, uploadUri, exchangeToken);
                    break;

                case "pending":
                case "converting":
                case "running":
                    await Task.Delay(PollInterval, exchangeToken);
                    break;

                case "done":
                    return MinerUHttpProtocol.ValidateSignedUri(fullZipUrl, mode.Operation("result"));

                case "failed":
                    throw new ProviderException(
                        $"mineru-{mode.Operation("task-failed")}",
                        $"The MinerU Cloud {mode.Name} extraction task failed.",
                        ProviderFailureCategory.Permanent);

                default:
                    throw new ProviderException(
                        $"mineru-{mode.Operation("task-state-unknown")}",
                        $"The MinerU Cloud {mode.Name} extraction task returned an unknown state.",
                        ProviderFailureCategory.Transient);
            }
        }
    }

    private async Task<(string State, string? FullZipUrl)> GetBatchStateAsync(
        ProviderExecutionConfiguration cloud,
        string batchId,
        CancellationToken exchangeToken)
    {
        var operation = mode.Operation("status");
        var taskId = MinerUHttpProtocol.ValidateExternalTaskId(batchId);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            MinerUHttpProtocol.BuildEndpoint(
                cloud.BaseUri,
                $"api/v4/extract-results/batch/{taskId}"));
        MinerUHttpProtocol.AddBearerCredential(message, cloud.Credential, required: true);

        using var response = await MinerUHttpProtocol.SendAsync(
            providerApiClient,
            message,
            HttpCompletionOption.ResponseHeadersRead,
            operation,
            exchangeToken);
        MinerUHttpProtocol.EnsureSuccess(response, operation);
        using var body = await MinerUHttpProtocol.ReadJsonAsync(response, operation, exchangeToken);
        var data = MinerUHttpProtocol.ReadSuccessfulData(body.RootElement, operation);
        if (!data.TryGetProperty("extract_result", out var extractResults)
            || extractResults.ValueKind != JsonValueKind.Array
            || extractResults.GetArrayLength() != 1
            || extractResults[0].ValueKind != JsonValueKind.Object)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                operation,
                $"The MinerU Cloud {mode.Name} status response did not contain one file result.");
        }

        var result = extractResults[0];
        return (
            MinerUHttpProtocol.ReadRequiredCloudString(result, "state", operation),
            result.TryGetProperty("full_zip_url", out var url)
                && url.ValueKind == JsonValueKind.String
                    ? url.GetString()
                    : null);
    }

    /// <summary>
    /// The signed transfer rules the full-parsing upload follows: a bare PUT with a Content-Length,
    /// no Provider credential, no Content-Type, through the dedicated SSRF-guarded client.
    /// </summary>
    private async Task UploadAsync(
        LayoutDetectionRequest request,
        Uri uploadUri,
        CancellationToken exchangeToken)
    {
        var operation = mode.Operation("upload");
        using var message = new HttpRequestMessage(HttpMethod.Put, uploadUri);
        var content = new ByteArrayContent(request.Image.ToArray());
        content.Headers.ContentLength = request.Image.Length;
        message.Content = content;

        using var response = await MinerUHttpProtocol.SendAsync(
            signedTransferClient,
            message,
            HttpCompletionOption.ResponseHeadersRead,
            operation,
            exchangeToken);
        MinerUHttpProtocol.EnsureSuccess(response, operation, ProviderFailureCategory.Permanent);
    }

    /// <summary>
    /// Downloads the result archive into one bounded in-memory buffer. The non-archiving modes
    /// create no temporary files on any path, so memory is the whole budget and the bound is
    /// checked while copying, not after.
    /// </summary>
    private async Task<ZipArchive> DownloadArchiveAsync(Uri resultUri, CancellationToken exchangeToken)
    {
        var operation = mode.Operation("result");
        using var message = new HttpRequestMessage(HttpMethod.Get, resultUri);
        var response = await MinerUHttpProtocol.SendAsync(
            signedTransferClient,
            message,
            HttpCompletionOption.ResponseHeadersRead,
            operation,
            exchangeToken);
        try
        {
            MinerUHttpProtocol.EnsureSuccess(response, operation, ProviderFailureCategory.Permanent);
            var content = await response.Content.ReadAsStreamAsync(exchangeToken);
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await content.ReadAsync(chunk, exchangeToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaximumArchiveBytes)
                {
                    throw new ProviderException(
                        $"mineru-{mode.Operation("archive-too-large")}",
                        $"The MinerU Cloud {mode.Name} result archive exceeded its size limit.",
                        ProviderFailureCategory.Permanent);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), exchangeToken);
            }

            if (buffer.Length == 0)
            {
                throw MinerUHttpProtocol.InvalidCloudResponse(
                    operation,
                    $"The MinerU Cloud {mode.Name} result archive is empty.");
            }

            response.Dispose();
            buffer.Position = 0;
            return new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static string FileNameFor(string mediaType) =>
        string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? "page.jpg" : "page.png";

    /// <summary>
    /// Reads the cloud's <c>layout.json</c> (its middle JSON) out of the archive and validates the
    /// one page every mode reduces: exactly one page whose <c>page_size</c> is the
    /// orientation-applied frame the cloud worked in, with proportions that match the submitted
    /// image.
    /// </summary>
    private MinerUCloudLayoutPage ReadPage(ZipArchive archive, LayoutDetectionRequest request)
    {
        var entry = FindLayoutEntry(archive);
        if (entry.Length > MaximumLayoutJsonBytes)
        {
            throw new ProviderException(
                $"mineru-{mode.Operation("layout-too-large")}",
                $"The MinerU Cloud {mode.Name} layout entry exceeded its size limit.",
                ProviderFailureCategory.Permanent);
        }

        byte[] layoutBytes;
        try
        {
            layoutBytes = new byte[entry.Length];
            using var stream = entry.Open();
            stream.ReadExactly(layoutBytes);
        }
        catch (InvalidDataException exception)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                mode.Operation("layout"),
                $"The MinerU Cloud {mode.Name} layout entry could not be read from the result archive.",
                ProviderFailureCategory.Permanent,
                exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(layoutBytes);
        }
        catch (JsonException exception)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                mode.Operation("layout"),
                $"The MinerU Cloud {mode.Name} layout entry is not valid JSON.",
                ProviderFailureCategory.Permanent,
                exception);
        }

        try
        {
            return MinerUCloudLayoutPage.Create(document, request, mode, MaximumAspectDrift);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private ZipArchiveEntry FindLayoutEntry(ZipArchive archive)
    {
        // The cloud names the file layout.json at the archive root; suffix matches cover the
        // per-document spelling some pipeline outputs use. More than one candidate is ambiguous,
        // none at all means the archive is not a layout-capable result.
        ZipArchiveEntry? exact = null;
        var suffixMatches = new List<ZipArchiveEntry>();
        foreach (var candidate in archive.Entries)
        {
            if (candidate.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(candidate.FullName, "layout.json", StringComparison.OrdinalIgnoreCase))
            {
                exact = candidate;
                break;
            }

            if (candidate.FullName.EndsWith("layout.json", StringComparison.OrdinalIgnoreCase)
                || candidate.FullName.EndsWith("middle.json", StringComparison.OrdinalIgnoreCase))
            {
                suffixMatches.Add(candidate);
            }
        }

        if (exact is not null)
        {
            return exact;
        }

        if (suffixMatches.Count > 1)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                mode.Operation("layout"),
                $"The MinerU Cloud {mode.Name} result archive contains ambiguous layout entries.");
        }

        if (suffixMatches.Count == 1)
        {
            return suffixMatches[0];
        }

        throw MinerUHttpProtocol.InvalidCloudResponse(
            mode.Operation("layout"),
            $"The MinerU Cloud {mode.Name} result archive contains no layout entry.");
    }
}

/// <summary>
/// The validated single page of a cloud <c>layout.json</c>, held for one reduction and disposed
/// with it. It owns the geometry rules both modes share: pixel boxes over <c>page_size</c> are
/// normalized, then mapped from the orientation-applied frame the cloud worked in back to the
/// original image's stored frame, so a consumer applying the reported EXIF orientation rotates
/// exactly once.
/// </summary>
internal sealed class MinerUCloudLayoutPage : IDisposable
{
    private readonly JsonDocument document;
    private readonly MinerUCloudExchangeMode mode;
    private readonly int orientation;

    private MinerUCloudLayoutPage(
        JsonDocument document,
        JsonElement page,
        double width,
        double height,
        int orientation,
        MinerUCloudExchangeMode mode)
    {
        this.document = document;
        Page = page;
        Width = width;
        Height = height;
        this.orientation = orientation;
        this.mode = mode;
    }

    public JsonElement Root => document.RootElement;

    public JsonElement Page { get; }

    public double Width { get; }

    public double Height { get; }

    public static MinerUCloudLayoutPage Create(
        JsonDocument document,
        LayoutDetectionRequest request,
        MinerUCloudExchangeMode mode,
        double maximumAspectDrift)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("pdf_info", out var pages)
            || pages.ValueKind != JsonValueKind.Array
            || pages.GetArrayLength() != 1
            || pages[0].ValueKind != JsonValueKind.Object)
        {
            throw Invalid(mode, "the layout entry does not describe exactly one page.");
        }

        var page = pages[0];
        if (!page.TryGetProperty("page_size", out var size)
            || size.ValueKind != JsonValueKind.Array
            || size.GetArrayLength() != 2
            || size[0].ValueKind != JsonValueKind.Number
            || size[1].ValueKind != JsonValueKind.Number
            || !size[0].TryGetDouble(out var width)
            || !size[1].TryGetDouble(out var height)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0
            || height <= 0)
        {
            throw Invalid(mode, "'page_size' is missing or is not a positive width/height pair.");
        }

        // The cloud works in the orientation-applied frame; a page whose proportions do not match
        // the image it was made from is a different frame, not a scaling of this one.
        var (orientedWidth, orientedHeight) = request.OrientedDimensions;
        var expectedAspect = (double)orientedWidth / orientedHeight;
        var actualAspect = width / height;
        if (Math.Abs(actualAspect - expectedAspect) / expectedAspect > maximumAspectDrift)
        {
            throw Invalid(mode, "the layout page proportions do not match the submitted image.");
        }

        return new MinerUCloudLayoutPage(
            document,
            page,
            width,
            height,
            request.ExifOrientation ?? 1,
            mode);
    }

    /// <summary>
    /// The version the cloud reports. The observed cloud page carries the field; the document root
    /// is accepted as the other place MinerU has spelled it.
    /// </summary>
    public string? Version
    {
        get
        {
            foreach (var source in new[] { Page, Root })
            {
                if (source.TryGetProperty("_version_name", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(version.GetString()))
                {
                    return version.GetString();
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Reads the <c>bbox</c> of <paramref name="element"/> as pixels over <c>page_size</c> and
    /// returns it normalized and mapped to the stored frame. <paramref name="where"/> names the
    /// element in the bounded rejection message.
    /// </summary>
    public (double X0, double Y0, double X1, double Y1) ReadStoredBox(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("bbox", out var box)
            || box.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{where} has no bbox.");
        }

        if (box.GetArrayLength() != 4
            || box.EnumerateArray().Any(value =>
                value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number)
                || !double.IsFinite(number)))
        {
            throw Invalid($"{where} has a malformed bbox.");
        }

        var x0 = box[0].GetDouble();
        var y0 = box[1].GetDouble();
        var x1 = box[2].GetDouble();
        var y1 = box[3].GetDouble();
        if (x0 < 0 || x0 > x1 || x1 > Width || y0 < 0 || y0 > y1 || y1 > Height)
        {
            throw Invalid($"{where} has a bbox outside the page.");
        }

        return ToStoredFrame(x0 / Width, y0 / Height, x1 / Width, y1 / Height, orientation);
    }

    /// <summary>
    /// The page's main blocks under <paramref name="preferredSpellings"/>, the first one present
    /// winning. A page with none of them is a page the model found nothing on (an explicitly empty
    /// answer, not a failure), and anything present but not an array is malformed.
    /// </summary>
    public bool TryGetBlocks(out JsonElement blocks, params string[] preferredSpellings)
    {
        foreach (var name in preferredSpellings)
        {
            if (!Page.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw Invalid($"'{name}' is not an array.");
            }

            if (value.GetArrayLength() > LayoutDetectionResult.MaximumRegionCount)
            {
                throw Invalid(
                    $"'{name}' holds more than {LayoutDetectionResult.MaximumRegionCount} entries.");
            }

            blocks = value;
            return true;
        }

        blocks = default;
        return false;
    }

    public ProviderException Invalid(string diagnostics) => Invalid(mode, diagnostics);

    public void Dispose() => document.Dispose();

    private static ProviderException Invalid(MinerUCloudExchangeMode mode, string diagnostics) =>
        MinerUHttpProtocol.InvalidCloudResponse(
            mode.Operation("layout"),
            $"The MinerU Cloud {mode.Name} output was rejected: {diagnostics}");

    /// <summary>
    /// The inverse of the EXIF orientation transform: takes a box in the orientation-applied
    /// (upright) frame the cloud worked in and returns the same box in the original image's stored
    /// frame. Every orientation is a composition of flips and quarter turns, all axis-preserving,
    /// so the mapping is the corner transform of the two box corners.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1) ToStoredFrame(
        double x0,
        double y0,
        double x1,
        double y1,
        int orientation) => orientation switch
        {
            1 => (x0, y0, x1, y1),
            2 => (1 - x1, y0, 1 - x0, y1),
            3 => (1 - x1, 1 - y1, 1 - x0, 1 - y0),
            4 => (x0, 1 - y1, x1, 1 - y0),
            5 => (y0, x0, y1, x1),
            6 => (y0, 1 - x1, y1, 1 - x0),
            7 => (1 - y1, 1 - x1, 1 - y0, 1 - x0),
            8 => (1 - y1, x0, 1 - y0, x1),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };
}
