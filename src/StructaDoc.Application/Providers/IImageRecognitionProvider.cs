namespace StructaDoc.Application.Providers;

/// <summary>
/// The image text-recognition execution contract (ADR-0015): a synchronous request/response
/// exchange that turns one page image into recognized blocks, in reading order, with their lines
/// and normalized boxes.
///
/// Like <see cref="ILayoutDetectionProvider"/>, this is deliberately not an
/// <see cref="IParseProvider"/> operation: it is a single non-archiving exchange that creates no
/// Document, Parse Run, Page, Block, Asset, Artifact, or request-result record. It is a separate
/// contract from layout detection, too, so a layout implementation can never start returning text
/// and a recognition implementation can never be selected by the layout backend setting.
///
/// The input is the same inspected page image the layout mode takes
/// (<see cref="LayoutDetectionRequest"/>): the two modes share their input rules and limits, and
/// the cloud implementation needs the same stored dimensions and EXIF orientation to report
/// geometry in the original image's stored frame.
///
/// The caller owns request validation, authorization, and the caller's deadline; the cancellation
/// token carries all three. Cancellation propagates as <see cref="OperationCanceledException"/>
/// rather than a Provider error. Malformed or incompatible backend output is reported as a bounded
/// <see cref="ProviderException"/> and never becomes a successful empty result.
/// </summary>
public interface IImageRecognitionProvider
{
    Task<ImageRecognitionResult> RecognizeAsync(
        LayoutDetectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Answers whether image text recognition is available and hands back the execution contract.
///
/// The mode is off unless an administrator turns it on (<c>Recognition:Enabled</c>, restart-bound),
/// and only the configured MinerU Cloud Provider can serve it. When no implementation is
/// registered — the mode is off — resolving throws <see cref="ProviderException"/> with the stable
/// <see cref="UnsupportedCapabilityErrorCode"/> and the Configuration category, before any byte is
/// handed to anything. There is no fallback to layout detection, to a local runtime, or to
/// document parsing.
/// </summary>
public interface IImageRecognitionProviderResolver
{
    const string UnsupportedCapabilityErrorCode = "recognition-capability-unsupported";

    IImageRecognitionProvider Resolve();
}

public sealed class ImageRecognitionProviderResolver : IImageRecognitionProviderResolver
{
    private readonly IImageRecognitionProvider? provider;

    public ImageRecognitionProviderResolver(IEnumerable<IImageRecognitionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var resolved = providers.ToArray();
        if (resolved.Any(candidate => candidate is null))
        {
            throw new ArgumentException("An image recognition provider cannot be null.", nameof(providers));
        }

        // Startup registers at most the one cloud implementation, so more than one is a
        // composition error rather than a choice to make here.
        if (resolved.Length > 1)
        {
            throw new InvalidOperationException(
                "More than one image recognition provider is registered.");
        }

        provider = resolved.SingleOrDefault();
    }

    public IImageRecognitionProvider Resolve() =>
        provider ?? throw new ProviderException(
            IImageRecognitionProviderResolver.UnsupportedCapabilityErrorCode,
            "Image text recognition is not enabled on this deployment. An administrator turns it on "
                + "with Recognition:Enabled; requests are not run through layout detection or "
                + "document parsing as a fallback.",
            ProviderFailureCategory.Configuration);
}
