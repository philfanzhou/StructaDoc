namespace StructaDoc.Application.Providers;

/// <summary>
/// The administrator-controlled layout inference runtime configuration, as the Application layer
/// sees it. This is runtime settings state (ADR-0011 foundation), not a Parse Run Provider
/// configuration: it is never snapshotted into a Parse Run, has no immutable version, and a
/// change reaches the running service at the next restart.
///
/// A <see langword="null"/> <see cref="BaseUri"/> means the runtime is not configured — the state
/// the capability resolution answers with an explicit unsupported error.
/// </summary>
public sealed record LayoutRuntimeConfiguration
{
    public LayoutRuntimeConfiguration(
        Uri? baseUri,
        ProviderCredential? credential,
        TimeSpan requestTimeout,
        int maxConcurrency)
    {
        if (baseUri is not null)
        {
            // The same address rule the Parse Run Provider configuration enforces, minus the
            // pieces a fixed inference route leaves no room for: the route is built beneath the
            // base, so a query would be silently ignored rather than honored.
            if (!baseUri.IsAbsoluteUri
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(baseUri.UserInfo)
                || !string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment))
            {
                throw new ArgumentException(
                    "The layout runtime Base URL must be an absolute HTTP(S) URI without user information, a query, or a fragment.",
                    nameof(baseUri));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(requestTimeout, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrency, 64);

        BaseUri = baseUri;
        Credential = credential;
        RequestTimeout = requestTimeout;
        MaxConcurrency = maxConcurrency;
    }

    /// <summary>The timeout bound the shipped defaults describe, restated by the settings catalog.</summary>
    public static TimeSpan DefaultRequestTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The configuration with no runtime behind it: every execution of it is unsupported.</summary>
    public static LayoutRuntimeConfiguration Unset { get; } =
        new(null, null, DefaultRequestTimeout, 1);

    public Uri? BaseUri { get; }

    public ProviderCredential? Credential { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaxConcurrency { get; }

    public override string ToString() =>
        $"LayoutRuntimeConfiguration {{ BaseUri = {BaseUri?.ToString() ?? "(unset)"}, Credential = [redacted], RequestTimeout = {RequestTimeout}, MaxConcurrency = {MaxConcurrency} }}";
}
