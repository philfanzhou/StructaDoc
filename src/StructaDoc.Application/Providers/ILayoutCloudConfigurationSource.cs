namespace StructaDoc.Application.Providers;

/// <summary>
/// Loads the per-request configuration snapshot for the MinerU Cloud layout backend: the enabled
/// default Provider configuration of type <c>mineru-cloud</c>, its current immutable version, and
/// its credential decrypted through the existing secret protector — the same snapshot shape the
/// Parse Run execution context builds, except it is held for one layout request and no record of
/// it is ever created.
///
/// The source is an Application-level seam on purpose: the layout execution contract stays clear
/// of the persistence and settings stores (an architecture test holds that), while the
/// implementation reads them wherever those stores live.
/// </summary>
public interface ILayoutCloudConfigurationSource
{
    Task<LayoutCloudConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Why a cloud layout configuration snapshot is or is not usable.</summary>
public enum LayoutCloudConfigurationState
{
    /// <summary>The snapshot is usable; <see cref="LayoutCloudConfigurationSnapshot.Configuration"/> is set.</summary>
    Ready,

    /// <summary>No Provider configuration is marked default at all.</summary>
    NoDefaultProvider,

    /// <summary>The default Provider configuration is not a MinerU Cloud one.</summary>
    DefaultProviderNotCloud,

    /// <summary>The default Provider configuration is disabled.</summary>
    DefaultProviderDisabled,

    /// <summary>The default Provider configuration carries no credential, which MinerU Cloud requires.</summary>
    CredentialMissing,
}

/// <summary>
/// One cloud layout configuration lookup. Every non-Ready state is a deployment matter, reported
/// as the explicit unsupported-capability configuration error rather than guessed around.
/// </summary>
public sealed record LayoutCloudConfigurationSnapshot
{
    public LayoutCloudConfigurationSnapshot(
        LayoutCloudConfigurationState state,
        ProviderExecutionConfiguration? configuration = null)
    {
        if (state == LayoutCloudConfigurationState.Ready != (configuration is not null))
        {
            throw new ArgumentException(
                "A Ready snapshot carries a configuration and no other state does.",
                nameof(configuration));
        }

        State = state;
        Configuration = configuration;
    }

    public LayoutCloudConfigurationState State { get; }

    public ProviderExecutionConfiguration? Configuration { get; }

    /// <summary>
    /// The one-sentence reason a person can act on, in this service's own words — the bounded text
    /// the unsupported-capability error carries.
    /// </summary>
    public string Describe() => State switch
    {
        LayoutCloudConfigurationState.Ready =>
            "The MinerU Cloud layout configuration is usable.",
        LayoutCloudConfigurationState.NoDefaultProvider =>
            "no Provider configuration is marked default, so there is no MinerU Cloud service to send layout requests to",
        LayoutCloudConfigurationState.DefaultProviderNotCloud =>
            "the default Provider configuration is not a MinerU Cloud one, so the cloud layout backend has nothing to use",
        LayoutCloudConfigurationState.DefaultProviderDisabled =>
            "the default Provider configuration is disabled, so the cloud layout backend refuses to send anything",
        LayoutCloudConfigurationState.CredentialMissing =>
            "the default Provider configuration carries no credential, and MinerU Cloud requires one",
        _ => "the MinerU Cloud layout configuration is not usable",
    };
}
