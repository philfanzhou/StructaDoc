namespace StructaDoc.Application.Authentication;

public interface IApiClientAdministrationService
{
    Task<IReadOnlyList<ApiClientRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<IssuedApiClient> CreateAsync(
        ApiClientDefinition definition,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<ApiClientMutationResult> UpdateAsync(
        Guid id,
        ApiClientDefinition definition,
        CancellationToken cancellationToken = default);

    Task<ApiClientCredentialMutationResult> RotateCredentialAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<ApiClientMutationStatus> RevokeAsync(
        Guid id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
