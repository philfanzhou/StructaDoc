namespace StructaDoc.Application.Providers;

public interface IProviderConfigAdministrationService
{
    Task<IReadOnlyList<ProviderConfigRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ProviderConfigMutationResult> CreateAsync(
        ProviderConfigDefinition definition,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<ProviderConfigMutationResult> UpdateAsync(
        Guid id,
        ProviderConfigDefinition definition,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
