namespace StructaDoc.Application.Providers;

/// <summary>
/// Answers whether a layout-capable backend is available for the explicitly selected
/// <see cref="LayoutBackend"/> and hands back the execution contract for it.
///
/// The capability is declared by this resolution step rather than by extending
/// <see cref="ProviderCapabilities"/>, which describes the persistent Parse Run flow. When no
/// implementation is registered for the selected backend, or the runtime backend's configuration
/// carries no Base URI, resolving throws <see cref="ProviderException"/> with the stable
/// <see cref="UnsupportedCapabilityErrorCode"/> and the Configuration category — the explicit
/// unsupported-capability error. There is no fallback and no guessing from a URL: a deployment
/// without the selected layout backend fails layout requests instead of quietly running them
/// through document parsing.
/// </summary>
public interface ILayoutDetectionProviderResolver
{
    const string UnsupportedCapabilityErrorCode = "layout-capability-unsupported";

    ILayoutDetectionProvider Resolve(LayoutBackendSelection selection);
}

public sealed class LayoutDetectionProviderResolver : ILayoutDetectionProviderResolver
{
    private readonly IReadOnlyCollection<ILayoutDetectionProvider> providers;

    public LayoutDetectionProviderResolver(IEnumerable<ILayoutDetectionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        // Startup registers exactly the selected backend's implementation, so more than one
        // registered implementation is a composition error: the selection has already been made
        // and there is nothing left to choose between them by.
        var resolved = providers.ToArray();
        if (resolved.Any(provider => provider is null))
        {
            throw new ArgumentException("A layout detection provider cannot be null.", nameof(providers));
        }

        if (resolved.Length > 1)
        {
            throw new InvalidOperationException(
                "More than one layout detection provider is registered.");
        }

        this.providers = resolved;
    }

    public ILayoutDetectionProvider Resolve(LayoutBackendSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var unsupported = selection.Backend switch
        {
            // The runtime backend's own unsupported state: its bound configuration carries no
            // address. The cloud backend's equivalent — an absent, disabled, non-cloud, or
            // credential-less default Provider — is decided by the cloud implementation's
            // configuration source, per request, because that is administrator state rather than
            // restart-bound settings.
            LayoutBackend.LayoutRuntime => selection.RuntimeConfiguration!.BaseUri is null,
            _ => false,
        };

        if (unsupported || providers.Count == 0)
        {
            throw new ProviderException(
                ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
                selection.Backend == LayoutBackend.MinerUCloud
                    ? "No MinerU Cloud layout backend is registered, so layout detection is unavailable. "
                        + "Select the cloud backend explicitly to use it; layout requests are not run through document parsing as a fallback."
                    : "No layout-capable runtime is configured, so layout detection is unavailable. "
                        + "Layout requests are not run through document parsing as a fallback.",
                ProviderFailureCategory.Configuration);
        }

        return providers.Single();
    }
}
