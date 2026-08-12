namespace StructaDoc.Application.Providers;

/// <summary>
/// What one optional Provider setting does when an administrator leaves it blank.
/// </summary>
/// <param name="IsUsed">
/// Whether the Provider type reads this setting at all. The two types accept the same configuration
/// shape but each ignores one of these fields, so a form that offers both offers one that does
/// nothing.
/// </param>
/// <param name="AppliedDefault">
/// The value this deployment sends when the setting is blank, or <see langword="null"/> when nothing
/// is sent and the Provider service applies its own default. The distinction matters to whoever is
/// deciding whether to fill the field in: one is answerable here, the other is not.
/// </param>
public sealed record ProviderSettingDescriptor(bool IsUsed, string? AppliedDefault)
{
    public static readonly ProviderSettingDescriptor Unused = new(false, null);
    public static readonly ProviderSettingDescriptor DecidedByService = new(true, null);
}

/// <summary>
/// What an administrator has to know before a Provider of this type can be configured.
/// </summary>
/// <param name="SuggestedBaseUrl">
/// The published address of the official service, or <see langword="null"/> when the address is
/// site-specific and only the deployment knows it.
/// </param>
/// <param name="RequiresCredential">
/// Whether every request this type makes carries a credential. A type that requires one is
/// unusable without it, so a configuration missing it is refused before a Parse Run is created
/// rather than left to fail against the service.
/// </param>
public sealed record ProviderTypeDescriptor(
    string ProviderType,
    string? SuggestedBaseUrl,
    bool RequiresCredential,
    ProviderSettingDescriptor Model,
    ProviderSettingDescriptor Backend);

/// <summary>
/// The single place that states what each Provider type defaults to. The adapters read these rather
/// than repeating the literals, so what the administration page shows as the default is the value
/// the outbound request actually carries; a form that advertised its own copy would keep saying so
/// after the adapter changed.
/// </summary>
public static class ProviderTypeDescriptors
{
    /// <summary>
    /// The hosted service's published API root. The adapters append their own paths to it, so this
    /// is the origin rather than any one route.
    /// </summary>
    public const string MinerUCloudBaseUrl = "https://mineru.net";

    /// <summary>
    /// What the Cloud protocol calls <c>model_version</c>. Cloud requires the field, so unlike the
    /// Local backend there is no "let the service decide": something has to be sent.
    /// </summary>
    public const string MinerUCloudDefaultModel = "pipeline";

    public static readonly ProviderTypeDescriptor MinerUCloud = new(
        ProviderTypes.MinerUCloud,
        MinerUCloudBaseUrl,
        // The hosted service authenticates every call, so a configuration without a token can only
        // produce failed Parse Runs.
        RequiresCredential: true,
        new ProviderSettingDescriptor(true, MinerUCloudDefaultModel),
        ProviderSettingDescriptor.Unused);

    public static readonly ProviderTypeDescriptor MinerULocal = new(
        ProviderTypes.MinerULocal,
        // A self-hosted service has no address anyone but its operator can know.
        null,
        // A self-hosted deployment decides whether it sits behind a token at all.
        RequiresCredential: false,
        ProviderSettingDescriptor.Unused,
        // The Local protocol omits the field entirely when it is blank, so the MinerU service picks.
        ProviderSettingDescriptor.DecidedByService);

    public static IReadOnlyList<ProviderTypeDescriptor> All { get; } =
        [MinerULocal, MinerUCloud];

    /// <summary>
    /// Whether a configuration of this type is unusable until a credential is stored. An unknown
    /// type answers <see langword="false"/>: it is not this method's place to block a Parse Run
    /// over a type it cannot describe.
    /// </summary>
    public static bool RequiresCredential(string providerType) =>
        All.FirstOrDefault(descriptor => string.Equals(
            descriptor.ProviderType,
            providerType,
            StringComparison.Ordinal))?.RequiresCredential == true;
}
