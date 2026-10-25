using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Persistence.Providers;

/// <summary>
/// Loads the MinerU Cloud layout backend's per-request configuration snapshot from the Provider
/// configuration store: the configuration marked default, its current immutable version, and its
/// credential decrypted through the existing secret protector — the same join the Parse Run
/// execution context performs, read once per layout request and never recorded anywhere.
///
/// The lookup is read-only and unsynchronized on purpose. Provider configuration versions are
/// immutable, so whatever version is current when the snapshot loads stays consistent for the
/// whole request even if an administrator rotates the default immediately afterwards; no version a
/// request uses can be deleted underneath it, because deletion is refused while any version is
/// referenced, and layout requests reference one only for the request's duration.
/// </summary>
public sealed class EfCoreLayoutCloudConfigurationSource(
    StructaDocDbContext dbContext,
    IProviderSecretProtector secretProtector) : ILayoutCloudConfigurationSource
{
    private const string DefaultMarker = "default";

    public async Task<LayoutCloudConfigurationSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await (
            from config in dbContext.ProviderConfigs.AsNoTracking()
            join version in dbContext.ProviderConfigVersions.AsNoTracking()
                on new { ConfigId = config.Id, VersionId = config.CurrentVersionId }
                equals new { ConfigId = version.ProviderConfigId, VersionId = version.Id }
            where config.DefaultMarker == DefaultMarker
            select new
            {
                config.Id,
                config.ProviderType,
                config.IsEnabled,
                VersionId = version.Id,
                version.BaseUrl,
                version.Model,
                version.Backend,
                version.ProtectedCredential,
            }).SingleOrDefaultAsync(cancellationToken);

        if (snapshot is null)
        {
            return new LayoutCloudConfigurationSnapshot(LayoutCloudConfigurationState.NoDefaultProvider);
        }

        // The type is checked before the enabled flag because a non-cloud default can never become
        // usable by enabling it; the credential is checked last because it is the piece an
        // administrator completes after the seeded configuration exists.
        if (!string.Equals(snapshot.ProviderType, ProviderTypes.MinerUCloud, StringComparison.Ordinal))
        {
            return new LayoutCloudConfigurationSnapshot(
                LayoutCloudConfigurationState.DefaultProviderNotCloud);
        }

        if (!snapshot.IsEnabled)
        {
            return new LayoutCloudConfigurationSnapshot(
                LayoutCloudConfigurationState.DefaultProviderDisabled);
        }

        if (snapshot.ProtectedCredential is null)
        {
            return new LayoutCloudConfigurationSnapshot(
                LayoutCloudConfigurationState.CredentialMissing);
        }

        return new LayoutCloudConfigurationSnapshot(
            LayoutCloudConfigurationState.Ready,
            new ProviderExecutionConfiguration(
                snapshot.Id,
                snapshot.VersionId,
                snapshot.ProviderType,
                new Uri(snapshot.BaseUrl, UriKind.Absolute),
                snapshot.Model,
                snapshot.Backend,
                new ProviderCredential(secretProtector.Unprotect(snapshot.ProtectedCredential))));
    }
}
