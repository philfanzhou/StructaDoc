namespace StructaDoc.Application.Providers;

/// <summary>
/// Answers whether a layout-capable backend is available for the given runtime configuration and
/// hands back the execution contract for it.
///
/// The capability is declared by this resolution step rather than by extending
/// <see cref="ProviderCapabilities"/>, which describes the persistent Parse Run flow. When the
/// runtime configuration is unset, or points at an engine no implementation is registered for,
/// resolving throws <see cref="ProviderException"/> with the stable
/// <see cref="UnsupportedCapabilityErrorCode"/> and the Configuration category — the explicit
/// unsupported-capability error. There is no fallback: the full-parsing providers never implement
/// the layout contract, so a deployment without a layout runtime fails layout requests instead of
/// quietly running them through document parsing.
/// </summary>
public interface ILayoutDetectionProviderResolver
{
    const string UnsupportedCapabilityErrorCode = "layout-capability-unsupported";

    ILayoutDetectionProvider Resolve(LayoutRuntimeConfiguration configuration);
}

public sealed class LayoutDetectionProviderResolver : ILayoutDetectionProviderResolver
{
    private readonly IReadOnlyCollection<ILayoutDetectionProvider> providers;

    public LayoutDetectionProviderResolver(IEnumerable<ILayoutDetectionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        // The runtime configuration carries no engine selector, so more than one registered
        // implementation is a composition error with nothing to choose between them by.
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

    public ILayoutDetectionProvider Resolve(LayoutRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.BaseUri is null || providers.Count == 0)
        {
            throw new ProviderException(
                ILayoutDetectionProviderResolver.UnsupportedCapabilityErrorCode,
                "No layout-capable runtime is configured, so layout detection is unavailable. "
                    + "Layout requests are not run through document parsing as a fallback.",
                ProviderFailureCategory.Configuration);
        }

        return providers.Single();
    }
}
