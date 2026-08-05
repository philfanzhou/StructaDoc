namespace StructaDoc.Application.Providers;

public sealed class ProviderExecutionConfiguration
{
    public ProviderExecutionConfiguration(
        Guid configId,
        Guid versionId,
        string providerType,
        Uri baseUri,
        string? model,
        string? backend,
        ProviderCredential? credential)
    {
        if (configId == Guid.Empty)
        {
            throw new ArgumentException("The Provider config ID is required.", nameof(configId));
        }

        if (versionId == Guid.Empty)
        {
            throw new ArgumentException("The Provider config version ID is required.", nameof(versionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException(
                "The Provider Base URI must be an absolute HTTP(S) URI without user information or a fragment.",
                nameof(baseUri));
        }

        ConfigId = configId;
        VersionId = versionId;
        ProviderType = providerType;
        BaseUri = baseUri;
        Model = model;
        Backend = backend;
        Credential = credential;
    }

    public Guid ConfigId { get; }

    public Guid VersionId { get; }

    public string ProviderType { get; }

    public Uri BaseUri { get; }

    public string? Model { get; }

    public string? Backend { get; }

    public ProviderCredential? Credential { get; }

    public override string ToString() =>
        $"ProviderExecutionConfiguration {{ ConfigId = {ConfigId}, VersionId = {VersionId}, ProviderType = {ProviderType}, Credential = [redacted] }}";
}
