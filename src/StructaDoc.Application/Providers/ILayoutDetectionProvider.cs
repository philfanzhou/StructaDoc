namespace StructaDoc.Application.Providers;

/// <summary>
/// The layout-detection execution contract: a synchronous request/response exchange that turns one
/// page image into normalized layout regions.
///
/// This is deliberately not an <see cref="IParseProvider"/> operation. The Parse Run flow is
/// persistent — capabilities, submission, checkpoints, polling, result streams — while the layout
/// mode is a single non-archiving exchange that creates no Document, Parse Run, or stored result
/// (ADR-0013). An implementation talks only to a layout-capable inference runtime: it must never
/// call a full-parsing task endpoint, per-region content recognition, or artifact generation, and
/// no <see cref="IParseProvider"/> implementation ever implements this contract, so no fallback
/// path from layout detection into full document parsing exists.
///
/// The caller owns request validation, authorization, and the caller's deadline; the cancellation
/// token carries all three. Cancellation propagates as <see cref="OperationCanceledException"/>
/// rather than a Provider error. Malformed or incomplete runtime output is reported as a bounded
/// <see cref="ProviderException"/> and never becomes a successful empty result.
/// </summary>
public interface ILayoutDetectionProvider
{
    Task<LayoutDetectionResult> DetectLayoutAsync(
        LayoutDetectionRequest request,
        CancellationToken cancellationToken = default);
}
