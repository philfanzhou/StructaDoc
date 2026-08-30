using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Adapters.Authentication;

public sealed class ApiClientAdministrationService(StructaDocDbContext dbContext)
    : IApiClientAdministrationService
{
    public async Task<IReadOnlyList<ApiClientRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var clients = await dbContext.ApiClients
            .AsNoTracking()
            .OrderByDescending(client => client.CreatedAtUtc)
            .ThenBy(client => client.Id)
            .Select(client => new ApiClientProjection(
                client.Id,
                client.Name,
                client.Scopes,
                client.IsActive,
                client.CreatedAtUtc,
                client.RevokedAtUtc))
            .ToListAsync(cancellationToken);
        return clients.Select(ToRecord).ToArray();
    }

    public async Task<IssuedApiClient> CreateAsync(
        ApiClientDefinition definition,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateUtc(nowUtc);

        var clientId = Guid.NewGuid();
        var issuedKey = ApiKeyCredential.Create(clientId);
        var client = new ApiClientEntity
        {
            Id = clientId,
            Name = definition.Name,
            SecretHash = issuedKey.SecretHash,
            Scopes = SerializeScopes(definition.Scopes),
            IsActive = true,
            CreatedAtUtc = nowUtc,
        };
        dbContext.ApiClients.Add(client);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new IssuedApiClient(ToRecord(client), issuedKey.Credential);
    }

    public async Task<ApiClientMutationResult> UpdateAsync(
        Guid id,
        ApiClientDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var candidate = await GetMutationCandidateAsync(id, cancellationToken);

        if (candidate is null)
        {
            return new ApiClientMutationResult(ApiClientMutationStatus.NotFound);
        }

        if (!candidate.IsActive || candidate.RevokedAtUtc is not null)
        {
            return new ApiClientMutationResult(ApiClientMutationStatus.Conflict);
        }

        var affected = await dbContext.ApiClients
            .Where(client =>
                client.Id == id
                && client.ConcurrencyVersion == candidate.ConcurrencyVersion
                && client.IsActive
                && client.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(client => client.Name, definition.Name)
                    .SetProperty(client => client.Scopes, SerializeScopes(definition.Scopes))
                    .SetProperty(
                        client => client.ConcurrencyVersion,
                        client => client.ConcurrencyVersion + 1),
                cancellationToken);

        if (affected != 1)
        {
            return new ApiClientMutationResult(ApiClientMutationStatus.Conflict);
        }

        return new ApiClientMutationResult(
            ApiClientMutationStatus.Succeeded,
            await GetRecordAsync(id, cancellationToken));
    }

    public async Task<ApiClientCredentialMutationResult> RotateCredentialAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var candidate = await GetMutationCandidateAsync(id, cancellationToken);

        if (candidate is null)
        {
            return new ApiClientCredentialMutationResult(ApiClientMutationStatus.NotFound);
        }

        if (!candidate.IsActive || candidate.RevokedAtUtc is not null)
        {
            return new ApiClientCredentialMutationResult(ApiClientMutationStatus.Conflict);
        }

        var issuedKey = ApiKeyCredential.Create(id);
        var affected = await dbContext.ApiClients
            .Where(client =>
                client.Id == id
                && client.ConcurrencyVersion == candidate.ConcurrencyVersion
                && client.IsActive
                && client.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(client => client.SecretHash, issuedKey.SecretHash)
                    .SetProperty(
                        client => client.ConcurrencyVersion,
                        client => client.ConcurrencyVersion + 1),
                cancellationToken);

        if (affected != 1)
        {
            return new ApiClientCredentialMutationResult(ApiClientMutationStatus.Conflict);
        }

        var client = await GetRecordAsync(id, cancellationToken);
        return new ApiClientCredentialMutationResult(
            ApiClientMutationStatus.Succeeded,
            new IssuedApiClient(client, issuedKey.Credential));
    }

    public async Task<ApiClientMutationStatus> RevokeAsync(
        Guid id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(nowUtc);
        var affected = await dbContext.ApiClients
            .Where(client => client.Id == id && client.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(client => client.IsActive, false)
                    .SetProperty(client => client.RevokedAtUtc, nowUtc)
                    .SetProperty(
                        client => client.ConcurrencyVersion,
                        client => client.ConcurrencyVersion + 1),
                cancellationToken);

        if (affected == 1)
        {
            return ApiClientMutationStatus.Succeeded;
        }

        return await dbContext.ApiClients.AnyAsync(
            client => client.Id == id,
            cancellationToken)
            ? ApiClientMutationStatus.Succeeded
            : ApiClientMutationStatus.NotFound;
    }

    private async Task<ApiClientRecord> GetRecordAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var client = await dbContext.ApiClients
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new ApiClientProjection(
                candidate.Id,
                candidate.Name,
                candidate.Scopes,
                candidate.IsActive,
                candidate.CreatedAtUtc,
                candidate.RevokedAtUtc))
            .SingleAsync(cancellationToken);
        return ToRecord(client);
    }

    private Task<ApiClientMutationCandidate?> GetMutationCandidateAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        return dbContext.ApiClients
            .AsNoTracking()
            .Where(client => client.Id == id)
            .Select(client => new ApiClientMutationCandidate(
                client.IsActive,
                client.RevokedAtUtc,
                client.ConcurrencyVersion))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static ApiClientRecord ToRecord(ApiClientEntity client)
    {
        return new ApiClientRecord(
            client.Id,
            client.Name,
            DeserializeScopes(client.Scopes),
            client.IsActive,
            client.CreatedAtUtc,
            client.RevokedAtUtc);
    }

    private static ApiClientRecord ToRecord(ApiClientProjection client)
    {
        return new ApiClientRecord(
            client.Id,
            client.Name,
            DeserializeScopes(client.Scopes),
            client.IsActive,
            client.CreatedAtUtc,
            client.RevokedAtUtc);
    }

    private static string SerializeScopes(IEnumerable<string> scopes)
    {
        return string.Join(' ', scopes);
    }

    private static IReadOnlyList<string> DeserializeScopes(string scopes)
    {
        return scopes.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("API Client timestamps must use UTC.", nameof(value));
        }
    }

    private sealed record ApiClientMutationCandidate(
        bool IsActive,
        DateTime? RevokedAtUtc,
        long ConcurrencyVersion);

    private sealed record ApiClientProjection(
        Guid Id,
        string Name,
        string Scopes,
        bool IsActive,
        DateTime CreatedAtUtc,
        DateTime? RevokedAtUtc);
}
