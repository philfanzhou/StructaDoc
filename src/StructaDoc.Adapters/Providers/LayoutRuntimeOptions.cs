using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

/// <summary>
/// The bound form of the <c>LayoutRuntime</c> settings section: where the optional layout
/// inference runtime listens, the credential a protected runtime requires, and the per-request
/// timeout and concurrency bound. Following <c>LibreOfficeConversionOptions</c>, the section binds
/// recoverably at startup, so a stored value that fails <see cref="Validate"/> is dropped and the
/// service starts on the shipped defaults.
///
/// A blank <see cref="BaseUrl"/> is not a validation failure: it is the shipped state in which no
/// layout runtime exists, which the capability resolution reports as an explicit unsupported
/// error.
/// </summary>
public sealed class LayoutRuntimeOptions
{
    public const string SectionName = "LayoutRuntime";

    public string BaseUrl { get; init; } = "";

    public string Credential { get; init; } = "";

    public TimeSpan Timeout { get; init; } = LayoutRuntimeConfiguration.DefaultRequestTimeout;

    public int MaxConcurrency { get; init; } = 1;

    public void Validate()
    {
        if (!string.IsNullOrEmpty(BaseUrl))
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(baseUri.UserInfo)
                || !string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment))
            {
                throw new InvalidOperationException(
                    "LayoutRuntime:BaseUrl must be an absolute HTTP(S) address without user information, a query, or a fragment.");
            }
        }

        if (!string.IsNullOrEmpty(Credential)
            && (string.IsNullOrWhiteSpace(Credential)
                || Credential.Length > 512
                || Credential.Any(char.IsControl)))
        {
            throw new InvalidOperationException(
                "LayoutRuntime:Credential must be a single-line value of at most 512 characters.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(Timeout, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrency);
    }

    public LayoutRuntimeConfiguration ToRuntimeConfiguration() => new(
        string.IsNullOrEmpty(BaseUrl) ? null : new Uri(BaseUrl, UriKind.Absolute),
        string.IsNullOrEmpty(Credential) ? null : new ProviderCredential(Credential),
        Timeout,
        MaxConcurrency);
}
