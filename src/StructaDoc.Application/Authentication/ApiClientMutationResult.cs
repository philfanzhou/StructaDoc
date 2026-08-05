namespace StructaDoc.Application.Authentication;

public enum ApiClientMutationStatus
{
    Succeeded,
    NotFound,
    Conflict,
}

public sealed record ApiClientMutationResult(
    ApiClientMutationStatus Status,
    ApiClientRecord? Client = null);

public sealed record ApiClientCredentialMutationResult(
    ApiClientMutationStatus Status,
    IssuedApiClient? IssuedClient = null);
