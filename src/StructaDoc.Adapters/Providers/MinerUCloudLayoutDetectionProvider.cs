using System.IO.Compression;
using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

/// <summary>
/// The MinerU Cloud layout adapter (ADR-0014): it serves the non-archiving layout-detection
/// exchange through the official cloud extraction API — the same signed batch upload, polling,
/// and result download protocol the full-parsing cloud adapter uses — and reduces the downloaded
/// result to normalized layout regions inside the one request.
///
/// The exchange is deliberately not a layout-only execution mode and never claims to be one: the
/// cloud service runs its full extraction pipeline for the submitted image — recognized text,
/// table HTML, and formula content are part of its answer — and this adapter keeps only the
/// layout of it. The bounded diagnostics slot says so on every successful response, because
/// "cloud execution skips recognition" is not a promise this backend can make. The submission
/// switches (<c>is_ocr</c>, <c>enable_table</c>, <c>enable_formula</c>) are sent as false to ask
/// for the least work, nothing more.
///
/// Nothing about the exchange persists: no Document, Parse Run, Page, Block, Asset, Artifact, or
/// request-result record exists, the cloud batch identifier lives in request memory only, and the
/// downloaded archive is one bounded in-memory buffer disposed with the request. No document or
/// Parse Run workflow runs and gets deleted afterwards — this is a separate execution contract.
/// </summary>
public sealed class MinerUCloudLayoutDetectionProvider : ILayoutDetectionProvider
{
    /// <summary>The deadline bound across the whole exchange that the shipped defaults describe.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The concurrency bound for cloud layout exchanges. It is fixed rather than settings-managed
    /// because the account behind the credential is shared with document parsing: four in-flight
    /// layout exchanges stay inside the endpoint's own in-flight cap while leaving the account
    /// room for Parse Runs.
    /// </summary>
    public const int MaxConcurrency = 4;

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
    private readonly TimeSpan deadline;
    private readonly SemaphoreSlim concurrencyLimit = new(MaxConcurrency, MaxConcurrency);

    public MinerUCloudLayoutDetectionProvider(
        HttpClient providerApiClient,
        HttpClient signedTransferClient,
        ILayoutCloudConfigurationSource configurationSource,
        TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(providerApiClient);
        ArgumentNullException.ThrowIfNull(signedTransferClient);
        ArgumentNullException.ThrowIfNull(configurationSource);
        var bound = deadline ?? DefaultDeadline;
        ArgumentOutOfRangeException.ThrowIfLessThan(bound, TimeSpan.FromMilliseconds(1));
        this.providerApiClient = providerApiClient;
        this.signedTransferClient = signedTransferClient;
        this.configurationSource = configurationSource;
        this.deadline = bound;
    }

    public async Task<LayoutDetectionResult> DetectLayoutAsync(
        LayoutDetectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // The configuration is a per-request snapshot of the default Provider configuration — the
        // same shape the Parse Run execution context builds, held for this request only and never
        // recorded anywhere. A deployment state that cannot serve the exchange is the explicit
        // unsupported-capability configuration error, exactly like an unconfigured runtime.
        var snapshot = await configurationSource.LoadAsync(cancellationToken);
        if (snapshot.State != LayoutCloudConfigurationState.Ready
            || snapshot.Configuration is not { } cloud)
        {
            throw new ProviderException(
                ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
                $"The MinerU Cloud layout backend is unavailable: {snapshot.Describe()}. "
                    + "Configure an enabled default MinerU Cloud Provider with its credential, or select the layout-runtime backend.",
                ProviderFailureCategory.Configuration);
        }

        MinerUHttpProtocol.ValidateConfiguration(cloud, ProviderTypes.MinerUCloud, requireHttps: true);
        if (request.StoredWidth is null || request.StoredHeight is null)
        {
            // The cloud route reports geometry against the original image, and only the caller's
            // inspection knows the original's stored dimensions. Reaching here is a wiring defect,
            // not a deployment state a message can repair.
            throw new ArgumentException(
                "The MinerU Cloud layout backend requires the request's inspected image dimensions.",
                nameof(request));
        }

        // The bound is held for the whole exchange, so a slow cloud exchange cannot accumulate
        // unlimited in-flight work no matter how many callers arrive.
        await concurrencyLimit.WaitAsync(cancellationToken);
        try
        {
            // One overall deadline across submission, upload, polling, download, and
            // normalization: the same budget covers every leg, an expired bound surfaces as the
            // stable layout timeout, and the caller's own cancellation still propagates.
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchange.CancelAfter(deadline);
            try
            {
                return await ExecuteAsync(cloud, request, exchange.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderException(
                    "mineru-layout-timeout",
                    "The MinerU Cloud layout exchange exceeded its overall deadline.",
                    ProviderFailureCategory.Transient);
            }
        }
        finally
        {
            concurrencyLimit.Release();
        }
    }

    private async Task<LayoutDetectionResult> ExecuteAsync(
        ProviderExecutionConfiguration cloud,
        LayoutDetectionRequest request,
        CancellationToken exchangeToken)
    {
        var (batchId, uploadUri) = await SubmitBatchAsync(cloud, request, exchangeToken);
        var resultUri = await DriveToCompletionAsync(
            cloud,
            request,
            batchId,
            uploadUri,
            exchangeToken);
        using var archive = await DownloadArchiveAsync(resultUri, exchangeToken);
        return Normalize(archive, request, cloud);
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
        // The least-work switches: the cloud service decides what its pipeline actually runs (the
        // observed answer executed recognition regardless), so these are a request, never an
        // advertisement of a layout-only mode.
        var file = new Dictionary<string, object>
        {
            ["name"] = FileNameFor(request.MediaType),
            ["data_id"] = Guid.NewGuid().ToString("N"),
            ["is_ocr"] = false,
        };
        var payload = new Dictionary<string, object>
        {
            ["files"] = new[] { file },
            ["model_version"] = cloud.Model ?? ProviderTypeDescriptors.MinerUCloudDefaultModel,
            ["enable_formula"] = false,
            ["enable_table"] = false,
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
            "layout-cloud-submit",
            exchangeToken);
        MinerUHttpProtocol.EnsureSuccess(response, "layout-cloud-submit");
        using var body = await MinerUHttpProtocol.ReadJsonAsync(
            response,
            "layout-cloud-submit",
            exchangeToken);
        var data = MinerUHttpProtocol.ReadSuccessfulData(body.RootElement, "layout-cloud-submit");
        var batchId = MinerUHttpProtocol.ReadRequiredCloudString(data, "batch_id", "layout-cloud-submit");
        MinerUHttpProtocol.ValidateExternalTaskId(batchId);
        if (!data.TryGetProperty("file_urls", out var fileUrls)
            || fileUrls.ValueKind != JsonValueKind.Array
            || fileUrls.GetArrayLength() != 1
            || fileUrls[0].ValueKind != JsonValueKind.String)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                "layout-cloud-submit",
                "The MinerU Cloud layout submission response did not contain one upload URL.");
        }

        var uploadUri = MinerUHttpProtocol.ValidateSignedUri(
            fileUrls[0].GetString(),
            "layout-cloud-upload");
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
                    return MinerUHttpProtocol.ValidateSignedUri(
                        fullZipUrl,
                        "layout-cloud-result");

                case "failed":
                    throw new ProviderException(
                        "mineru-layout-cloud-task-failed",
                        "The MinerU Cloud layout extraction task failed.",
                        ProviderFailureCategory.Permanent);

                default:
                    throw new ProviderException(
                        "mineru-layout-cloud-task-state-unknown",
                        "The MinerU Cloud layout extraction task returned an unknown state.",
                        ProviderFailureCategory.Transient);
            }
        }
    }

    private async Task<(string State, string? FullZipUrl)> GetBatchStateAsync(
        ProviderExecutionConfiguration cloud,
        string batchId,
        CancellationToken exchangeToken)
    {
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
            "layout-cloud-status",
            exchangeToken);
        MinerUHttpProtocol.EnsureSuccess(response, "layout-cloud-status");
        using var body = await MinerUHttpProtocol.ReadJsonAsync(
            response,
            "layout-cloud-status",
            exchangeToken);
        var data = MinerUHttpProtocol.ReadSuccessfulData(body.RootElement, "layout-cloud-status");
        if (!data.TryGetProperty("extract_result", out var extractResults)
            || extractResults.ValueKind != JsonValueKind.Array
            || extractResults.GetArrayLength() != 1
            || extractResults[0].ValueKind != JsonValueKind.Object)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                "layout-cloud-status",
                "The MinerU Cloud layout status response did not contain one file result.");
        }

        var result = extractResults[0];
        return (
            MinerUHttpProtocol.ReadRequiredCloudString(result, "state", "layout-cloud-status"),
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
        using var message = new HttpRequestMessage(HttpMethod.Put, uploadUri);
        var content = new ByteArrayContent(request.Image.ToArray());
        content.Headers.ContentLength = request.Image.Length;
        message.Content = content;

        using var response = await MinerUHttpProtocol.SendAsync(
            signedTransferClient,
            message,
            HttpCompletionOption.ResponseHeadersRead,
            "layout-cloud-upload",
            exchangeToken);
        MinerUHttpProtocol.EnsureSuccess(
            response,
            "layout-cloud-upload",
            ProviderFailureCategory.Permanent);
    }

    /// <summary>
    /// Downloads the result archive into one bounded in-memory buffer. The layout mode creates no
    /// temporary files on any path, so memory is the whole budget and the bound is checked while
    /// copying, not after.
    /// </summary>
    private async Task<ZipArchive> DownloadArchiveAsync(Uri resultUri, CancellationToken exchangeToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, resultUri);
        var response = await MinerUHttpProtocol.SendAsync(
            signedTransferClient,
            message,
            HttpCompletionOption.ResponseHeadersRead,
            "layout-cloud-result",
            exchangeToken);
        try
        {
            MinerUHttpProtocol.EnsureSuccess(
                response,
                "layout-cloud-result",
                ProviderFailureCategory.Permanent);
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
                        "mineru-layout-cloud-archive-too-large",
                        "The MinerU Cloud layout result archive exceeded its size limit.",
                        ProviderFailureCategory.Permanent);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), exchangeToken);
            }

            if (buffer.Length == 0)
            {
                throw MinerUHttpProtocol.InvalidCloudResponse(
                    "layout-cloud-result",
                    "The MinerU Cloud layout result archive is empty.");
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
    /// Reduces the cloud's layout JSON to the public layout contract. The file this reads is the
    /// cloud's <c>layout.json</c> (its middle JSON): one page whose <c>page_size</c> is the
    /// orientation-applied frame the cloud worked in, whose main blocks carry pixel boxes over that
    /// page size, and whose nested line/span entries are deliberately not read — they are the same
    /// regions' contents, not more regions. Geometry is normalized against <c>page_size</c>, then
    /// mapped from the orientation-applied frame back to the original image's stored frame, so the
    /// response honors the public contract and a consumer applying the reported EXIF orientation
    /// rotates exactly once.
    /// </summary>
    private static LayoutDetectionResult Normalize(
        ZipArchive archive,
        LayoutDetectionRequest request,
        ProviderExecutionConfiguration cloud)
    {
        var entry = FindLayoutEntry(archive);
        byte[] layoutBytes;
        if (entry.Length > MaximumLayoutJsonBytes)
        {
            throw new ProviderException(
                "mineru-layout-cloud-layout-too-large",
                "The MinerU Cloud layout entry exceeded its size limit.",
                ProviderFailureCategory.Permanent);
        }

        try
        {
            layoutBytes = new byte[entry.Length];
            using var stream = entry.Open();
            stream.ReadExactly(layoutBytes);
        }
        catch (InvalidDataException exception)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                "layout-cloud-layout",
                "The MinerU Cloud layout entry could not be read from the result archive.",
                ProviderFailureCategory.Permanent,
                exception);
        }

        using var document = ParseLayoutJson(layoutBytes);
        return BuildResult(document.RootElement, request, cloud);
    }

    private static ZipArchiveEntry FindLayoutEntry(ZipArchive archive)
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
                "layout-cloud-layout",
                "The MinerU Cloud layout result archive contains ambiguous layout entries.");
        }

        if (suffixMatches.Count == 1)
        {
            return suffixMatches[0];
        }

        throw MinerUHttpProtocol.InvalidCloudResponse(
            "layout-cloud-layout",
            "The MinerU Cloud layout result archive contains no layout entry.");
    }

    private static JsonDocument ParseLayoutJson(byte[] layoutBytes)
    {
        try
        {
            return JsonDocument.Parse(layoutBytes);
        }
        catch (JsonException exception)
        {
            throw MinerUHttpProtocol.InvalidCloudResponse(
                "layout-cloud-layout",
                "The MinerU Cloud layout entry is not valid JSON.",
                ProviderFailureCategory.Permanent,
                exception);
        }
    }

    private static LayoutDetectionResult BuildResult(
        JsonElement root,
        LayoutDetectionRequest request,
        ProviderExecutionConfiguration cloud)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("pdf_info", out var pages)
            || pages.ValueKind != JsonValueKind.Array
            || pages.GetArrayLength() != 1
            || pages[0].ValueKind != JsonValueKind.Object)
        {
            throw InvalidLayout("the layout entry does not describe exactly one page.");
        }

        var page = pages[0];
        var (pageWidth, pageHeight) = ReadPageSize(page);

        // The cloud works in the orientation-applied frame; a page whose proportions do not match
        // the image it was made from is a different frame, not a scaling of this one.
        var (orientedWidth, orientedHeight) = request.OrientedDimensions;
        var expectedAspect = (double)orientedWidth / orientedHeight;
        var actualAspect = pageWidth / pageHeight;
        if (Math.Abs(actualAspect - expectedAspect) / expectedAspect > MaximumAspectDrift)
        {
            throw InvalidLayout("the layout page proportions do not match the submitted image.");
        }

        var orientation = request.ExifOrientation ?? 1;
        var regions = new List<LayoutDetectionRegion>();
        if (TryGetMainBlocks(page, out var blocks))
        {
            var index = 0;
            foreach (var block in blocks.EnumerateArray())
            {
                var label = ReadLabel(block, index);
                var (x0, y0, x1, y1) = ReadPixelBox(block, index, pageWidth, pageHeight);
                var stored = ToStoredFrame(x0, y0, x1, y1, orientation);
                try
                {
                    regions.Add(new LayoutDetectionRegion(
                        label,
                        stored.X0,
                        stored.Y0,
                        stored.X1,
                        stored.Y1));
                }
                catch (ArgumentException)
                {
                    throw InvalidLayout(
                        $"the block at index {index} has a label or box outside the layout contract.");
                }

                index++;
            }
        }

        var model = cloud.Model ?? ProviderTypeDescriptors.MinerUCloudDefaultModel;
        try
        {
            return new LayoutDetectionResult(
                regions,
                request.StoredWidth!.Value,
                request.StoredHeight!.Value,
                engine: ProviderTypes.MinerUCloud,
                model: model,
                version: ReadOptionalVersion(root, page),
                diagnostics:
                [
                    "The MinerU Cloud backend executed its full extraction pipeline; only layout regions are returned.",
                ]);
        }
        catch (ArgumentException exception)
        {
            throw InvalidLayout(exception.Message);
        }
    }

    private static (double Width, double Height) ReadPageSize(JsonElement page)
    {
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
            throw InvalidLayout("'page_size' is missing or is not a positive width/height pair.");
        }

        return (width, height);
    }

    /// <summary>
    /// The page's main blocks. MinerU has spelled the array <c>preproc_blocks</c> and
    /// <c>para_blocks</c> across versions; both are accepted, a page with neither is a page the
    /// model found nothing on (an explicitly empty answer, not a failure), and anything present
    /// but not an array is malformed. Nested <c>lines</c>/<c>spans</c> entries inside a block are
    /// the same region's contents and are never read as regions of their own.
    /// </summary>
    private static bool TryGetMainBlocks(JsonElement page, out JsonElement blocks)
    {
        if (page.TryGetProperty("preproc_blocks", out var preproc))
        {
            blocks = RequireBlockArray(preproc, "preproc_blocks");
            return true;
        }

        if (page.TryGetProperty("para_blocks", out var para))
        {
            blocks = RequireBlockArray(para, "para_blocks");
            return true;
        }

        blocks = default;
        return false;
    }

    private static JsonElement RequireBlockArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidLayout($"'{name}' is not an array.");
        }

        if (value.GetArrayLength() > LayoutDetectionResult.MaximumRegionCount)
        {
            throw InvalidLayout(
                $"'{name}' holds more than {LayoutDetectionResult.MaximumRegionCount} entries.");
        }

        return value;
    }

    private static string ReadLabel(JsonElement block, int index)
    {
        if (block.ValueKind != JsonValueKind.Object
            || !block.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(type.GetString()))
        {
            throw InvalidLayout($"the block at index {index} has no type.");
        }

        return MapLabel(type.GetString()!);
    }

    private static (double X0, double Y0, double X1, double Y1) ReadPixelBox(
        JsonElement block,
        int index,
        double pageWidth,
        double pageHeight)
    {
        if (!block.TryGetProperty("bbox", out var box) || box.ValueKind != JsonValueKind.Array)
        {
            throw InvalidLayout($"the block at index {index} has no bbox.");
        }

        var values = box.EnumerateArray().ToArray();
        if (values.Length != 4
            || values.Any(value =>
                value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number)
                || !double.IsFinite(number)))
        {
            throw InvalidLayout($"the block at index {index} has a malformed bbox.");
        }

        var x0 = values[0].GetDouble();
        var y0 = values[1].GetDouble();
        var x1 = values[2].GetDouble();
        var y1 = values[3].GetDouble();
        if (x0 < 0 || x0 > x1 || x1 > pageWidth || y0 < 0 || y0 > y1 || y1 > pageHeight)
        {
            throw InvalidLayout($"the block at index {index} has a bbox outside the page.");
        }

        return (x0 / pageWidth, y0 / pageHeight, x1 / pageWidth, y1 / pageHeight);
    }

    /// <summary>
    /// The generic category vocabulary, normalized the same way the result normalizer normalizes
    /// MinerU tokens. A type outside the mapped set stays as its own generic label when it fits the
    /// contract's identifier rules — peripheral categories (an aside, an index) are preserved
    /// rather than collapsed — and is rejected when it does not.
    /// </summary>
    private static string MapLabel(string providerType)
    {
        var normalized = providerType.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "title" or "heading" => "title",
            "text" or "paragraph" => "text",
            "list" or "list-item" => "list",
            "table" => "table",
            "equation" or "formula" or "inline-equation" or "interline-equation" => "formula",
            "image" or "figure" or "chart" => "image",
            "code" or "algorithm" => "code",
            "header" or "page-header" => "header",
            "footer" or "page-footer" => "footer",
            "footnote" or "page-footnote" => "footnote",
            "page-number" => "page-number",
            _ => normalized,
        };
    }

    private static string? ReadOptionalVersion(JsonElement root, JsonElement page)
    {
        // The observed cloud page carries the field; the document root is accepted as the other
        // place MinerU has spelled it.
        foreach (var source in new[] { page, root })
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

    private static ProviderException InvalidLayout(string diagnostics) =>
        MinerUHttpProtocol.InvalidCloudResponse(
            "layout-cloud-layout",
            $"The MinerU Cloud layout output was rejected: {diagnostics}");
}
