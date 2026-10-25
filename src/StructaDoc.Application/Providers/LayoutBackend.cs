namespace StructaDoc.Application.Providers;

/// <summary>
/// Which backend serves the non-archiving layout-detection mode. The choice is an administrator
/// setting bound once at startup (<c>Layout:Backend</c>), not something a request or a fallback
/// decides: the optional local inference runtime of ADR-0013 stays the default, and routing the
/// exchange through the configured MinerU Cloud Provider is a deliberate selection recorded by
/// ADR-0014.
/// </summary>
public enum LayoutBackend
{
    /// <summary>
    /// The optional layout inference runtime configured through the <c>LayoutRuntime</c> section —
    /// the shipped default. An absent value means this backend, so a deployment that never heard of
    /// the cloud route keeps its exact behavior.
    /// </summary>
    LayoutRuntime = 0,

    /// <summary>
    /// The configured MinerU Cloud Provider, through the official cloud extraction API. The cloud
    /// service executes its full extraction pipeline for the submitted image; this service reduces
    /// the downloaded result to layout regions inside the request and archives nothing.
    /// </summary>
    MinerUCloud = 1,
}

/// <summary>The wire spellings of the backend setting, shared by the catalog and the options.</summary>
public static class LayoutBackendNames
{
    public const string LayoutRuntime = "layout-runtime";

    public const string MinerUCloud = "mineru-cloud";

    public static bool IsKnown(string value) =>
        value is LayoutRuntime or MinerUCloud;
}

/// <summary>
/// What the capability resolution resolves against: the explicitly selected backend together with
/// the configuration of that backend. The runtime backend carries its bound
/// <see cref="LayoutRuntimeConfiguration"/> (a null Base URI is the unsupported state); the cloud
/// backend carries nothing here, because its configuration is a per-request snapshot the cloud
/// implementation loads itself from <see cref="ILayoutCloudConfigurationSource"/> — the Provider
/// configuration is administrator state that can change between requests, unlike the
/// restart-bound runtime section.
/// </summary>
public sealed record LayoutBackendSelection
{
    public LayoutBackendSelection(LayoutBackend backend, LayoutRuntimeConfiguration? runtimeConfiguration)
    {
        if (backend == LayoutBackend.LayoutRuntime && runtimeConfiguration is null)
        {
            throw new ArgumentException(
                "The layout runtime backend requires its runtime configuration.",
                nameof(runtimeConfiguration));
        }

        if (backend == LayoutBackend.MinerUCloud && runtimeConfiguration is not null)
        {
            throw new ArgumentException(
                "The MinerU Cloud layout backend takes no runtime configuration.",
                nameof(runtimeConfiguration));
        }

        Backend = backend;
        RuntimeConfiguration = runtimeConfiguration;
    }

    public static LayoutBackendSelection ForRuntime(LayoutRuntimeConfiguration runtimeConfiguration) =>
        new(LayoutBackend.LayoutRuntime, runtimeConfiguration);

    public static LayoutBackendSelection ForCloud() => new(LayoutBackend.MinerUCloud, null);

    public LayoutBackend Backend { get; }

    public LayoutRuntimeConfiguration? RuntimeConfiguration { get; }
}
