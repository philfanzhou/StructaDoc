namespace StructaDoc.Application.Providers;

public sealed record ProviderConfigDefinition
{
    public const int MaximumNameLength = 255;
    public const int MaximumEndpointLength = 2048;
    public const int MaximumSettingLength = 255;
    public const int MaximumCredentialLength = 4096;

    private ProviderConfigDefinition(
        string name,
        string providerType,
        string baseUrl,
        string? model,
        string? backend,
        string? credential,
        bool clearCredential,
        bool isEnabled,
        bool isDefault)
    {
        Name = name;
        ProviderType = providerType;
        BaseUrl = baseUrl;
        Model = model;
        Backend = backend;
        Credential = credential;
        ClearCredential = clearCredential;
        IsEnabled = isEnabled;
        IsDefault = isDefault;
    }

    public string Name { get; }
    public string ProviderType { get; }
    public string BaseUrl { get; }
    public string? Model { get; }
    public string? Backend { get; }
    public string? Credential { get; }
    public bool ClearCredential { get; }
    public bool IsEnabled { get; }
    public bool IsDefault { get; }

    public static bool TryCreate(
        string? name,
        string? providerType,
        string? baseUrl,
        string? model,
        string? backend,
        string? credential,
        bool clearCredential,
        bool isEnabled,
        bool isDefault,
        out ProviderConfigDefinition? definition,
        out string errorField,
        out string errorMessage)
    {
        definition = null;
        var normalizedName = name?.Trim();
        var normalizedProviderType = providerType?.Trim();
        var normalizedBaseUrl = baseUrl?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName)
            || normalizedName.Length > MaximumNameLength)
        {
            return Fail(
                "name",
                $"Provider name must contain between 1 and {MaximumNameLength} characters.",
                out errorField,
                out errorMessage);
        }

        if (normalizedProviderType is null || !ProviderTypes.IsKnown(normalizedProviderType))
        {
            return Fail(
                "providerType",
                $"Provider type must be '{ProviderTypes.MinerUCloud}' or '{ProviderTypes.MinerULocal}'.",
                out errorField,
                out errorMessage);
        }

        if (string.IsNullOrWhiteSpace(normalizedBaseUrl)
            || normalizedBaseUrl.Length > MaximumEndpointLength
            || !Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return Fail(
                "baseUrl",
                "Provider Base URL must be an absolute HTTP(S) URL without user information or a fragment.",
                out errorField,
                out errorMessage);
        }

        if (!TryNormalizeOptional(model, MaximumSettingLength, out var normalizedModel))
        {
            return Fail(
                "model",
                $"Provider model cannot exceed {MaximumSettingLength} characters.",
                out errorField,
                out errorMessage);
        }

        if (!TryNormalizeOptional(backend, MaximumSettingLength, out var normalizedBackend))
        {
            return Fail(
                "backend",
                $"Provider backend cannot exceed {MaximumSettingLength} characters.",
                out errorField,
                out errorMessage);
        }

        if (credential is not null
            && (string.IsNullOrWhiteSpace(credential)
                || credential.Length > MaximumCredentialLength))
        {
            return Fail(
                "credential",
                $"Provider credential must contain between 1 and {MaximumCredentialLength} characters when supplied.",
                out errorField,
                out errorMessage);
        }

        if (credential is not null && clearCredential)
        {
            return Fail(
                "credential",
                "Credential and clearCredential cannot be supplied together.",
                out errorField,
                out errorMessage);
        }

        if (!isEnabled && isDefault)
        {
            return Fail(
                "isDefault",
                "A disabled Provider cannot be the default.",
                out errorField,
                out errorMessage);
        }

        definition = new ProviderConfigDefinition(
            normalizedName,
            normalizedProviderType,
            endpoint.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped),
            normalizedModel,
            normalizedBackend,
            credential,
            clearCredential,
            isEnabled,
            isDefault);
        errorField = string.Empty;
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryNormalizeOptional(
        string? value,
        int maximumLength,
        out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= maximumLength;
    }

    private static bool Fail(
        string field,
        string message,
        out string errorField,
        out string errorMessage)
    {
        errorField = field;
        errorMessage = message;
        return false;
    }
}
