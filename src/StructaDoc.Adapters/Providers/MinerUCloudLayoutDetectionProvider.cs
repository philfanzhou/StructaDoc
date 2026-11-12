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
    public static readonly TimeSpan DefaultDeadline = MinerUCloudImageExchange.DefaultDeadline;

    /// <summary>
    /// The concurrency bound for cloud image exchanges, shared with the recognition mode through
    /// <see cref="MinerUCloudExchangeLimiter"/>: the account behind the credential is shared with
    /// document parsing, so four in-flight image exchanges stay inside the endpoint's own in-flight
    /// cap while leaving the account room for Parse Runs.
    /// </summary>
    public const int MaxConcurrency = MinerUCloudExchangeLimiter.MaxConcurrency;

    /// <summary>
    /// The bound on the downloaded result archive. One page image cannot legitimately approach it;
    /// it exists so a hostile or broken result endpoint cannot describe an unbounded download.
    /// </summary>
    public const long MaximumArchiveBytes = MinerUCloudImageExchange.MaximumArchiveBytes;

    /// <summary>The bound on the single layout JSON entry read out of that archive.</summary>
    public const long MaximumLayoutJsonBytes = MinerUCloudImageExchange.MaximumLayoutJsonBytes;

    /// <summary>
    /// The least-work switches (<c>is_ocr</c>, <c>enable_table</c>, <c>enable_formula</c> all
    /// false) and the layout mode's stable code family.
    /// </summary>
    private static readonly MinerUCloudExchangeMode Mode = new(
        "layout",
        ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
        "Configure an enabled default MinerU Cloud Provider with its credential, or select the layout-runtime backend.",
        IsOcr: false,
        EnableTable: false,
        EnableFormula: false);

    private readonly MinerUCloudImageExchange exchange;

    /// <param name="limiter">
    /// The Host's shared cloud exchange bound. A provider built without one (a standalone use, such
    /// as a test) gets a private bound of the same size.
    /// </param>
    public MinerUCloudLayoutDetectionProvider(
        HttpClient providerApiClient,
        HttpClient signedTransferClient,
        ILayoutCloudConfigurationSource configurationSource,
        TimeSpan? deadline = null,
        MinerUCloudExchangeLimiter? limiter = null)
    {
        exchange = new MinerUCloudImageExchange(
            providerApiClient,
            signedTransferClient,
            configurationSource,
            limiter ?? new MinerUCloudExchangeLimiter(),
            deadline,
            Mode);
    }

    public Task<LayoutDetectionResult> DetectLayoutAsync(
        LayoutDetectionRequest request,
        CancellationToken cancellationToken = default) =>
        exchange.RunAsync(request, (page, cloud) => BuildResult(page, request, cloud), cancellationToken);

    /// <summary>
    /// Reduces the cloud's layout JSON to the public layout contract: the page's main blocks become
    /// regions, and their nested line/span entries are deliberately not read — they are the same
    /// regions' contents, not more regions.
    /// </summary>
    private static LayoutDetectionResult BuildResult(
        MinerUCloudLayoutPage page,
        LayoutDetectionRequest request,
        ProviderExecutionConfiguration cloud)
    {
        var regions = new List<LayoutDetectionRegion>();
        // MinerU has spelled the main block array `preproc_blocks` and `para_blocks` across
        // versions; both are accepted.
        if (page.TryGetBlocks(out var blocks, "preproc_blocks", "para_blocks"))
        {
            var index = 0;
            foreach (var block in blocks.EnumerateArray())
            {
                var label = ReadLabel(page, block, index);
                var stored = page.ReadStoredBox(block, $"the block at index {index}");
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
                    throw page.Invalid(
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
                version: page.Version,
                diagnostics:
                [
                    "The MinerU Cloud backend executed its full extraction pipeline; only layout regions are returned.",
                ]);
        }
        catch (ArgumentException exception)
        {
            throw page.Invalid(exception.Message);
        }
    }

    private static string ReadLabel(MinerUCloudLayoutPage page, JsonElement block, int index)
    {
        if (block.ValueKind != JsonValueKind.Object
            || !block.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(type.GetString()))
        {
            throw page.Invalid($"the block at index {index} has no type.");
        }

        return MapLabel(type.GetString()!);
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

    /// <summary>
    /// The inverse of the EXIF orientation transform: takes a box in the orientation-applied
    /// (upright) frame the cloud worked in and returns the same box in the original image's stored
    /// frame.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1) ToStoredFrame(
        double x0,
        double y0,
        double x1,
        double y1,
        int orientation) => MinerUCloudLayoutPage.ToStoredFrame(x0, y0, x1, y1, orientation);
}
