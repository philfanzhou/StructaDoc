using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Providers;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.Providers;

public sealed class EfCoreProviderConfigAdministrationService(
    StructaDocDbContext dbContext,
    IProviderSecretProtector secretProtector) : IProviderConfigAdministrationService
{
    private const string DefaultMarker = "default";

    public async Task<IReadOnlyList<ProviderConfigRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return await CurrentConfigs(orderByName: true).ToArrayAsync(cancellationToken);
    }

    public async Task<ProviderConfigMutationResult> CreateAsync(
        ProviderConfigDefinition definition,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var configId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync<ProviderConfigMutationResult>(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                if (definition.IsDefault)
                {
                    await ClearDefaultAsync(null, nowUtc, cancellationToken);
                }

                dbContext.ProviderConfigs.Add(new ProviderConfigEntity
                {
                    Id = configId,
                    Name = definition.Name,
                    ProviderType = definition.ProviderType,
                    IsEnabled = definition.IsEnabled,
                    DefaultMarker = definition.IsDefault ? DefaultMarker : null,
                    CurrentVersionId = versionId,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                dbContext.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
                {
                    Id = versionId,
                    ProviderConfigId = configId,
                    VersionNumber = 1,
                    BaseUrl = definition.BaseUrl,
                    Model = definition.Model,
                    Backend = definition.Backend,
                    ProtectedCredential = definition.Credential is null
                        ? null
                        : secretProtector.Protect(definition.Credential),
                    CreatedAtUtc = nowUtc,
                });
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(ProviderConfigMutationStatus.Succeeded, await GetCurrentAsync(configId, cancellationToken));
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                return new(ProviderConfigMutationStatus.Conflict);
            }
        });
    }

    public async Task<ProviderConfigMutationResult> UpdateAsync(
        Guid id,
        ProviderConfigDefinition definition,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var config = await dbContext.ProviderConfigs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (config is null)
        {
            return new(ProviderConfigMutationStatus.NotFound);
        }

        if (!string.Equals(config.ProviderType, definition.ProviderType, StringComparison.Ordinal))
        {
            return new(ProviderConfigMutationStatus.Conflict);
        }

        var currentVersion = await dbContext.ProviderConfigVersions
            .AsNoTracking()
            .SingleAsync(version => version.Id == config.CurrentVersionId, cancellationToken);
        var nextVersionId = Guid.NewGuid();

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync<ProviderConfigMutationResult>(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                if (definition.IsDefault)
                {
                    await ClearDefaultAsync(id, nowUtc, cancellationToken);
                }

                config.Name = definition.Name;
                config.IsEnabled = definition.IsEnabled;
                config.DefaultMarker = definition.IsDefault ? DefaultMarker : null;
                config.CurrentVersionId = nextVersionId;
                config.UpdatedAtUtc = nowUtc;
                dbContext.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
                {
                    Id = nextVersionId,
                    ProviderConfigId = id,
                    VersionNumber = checked(currentVersion.VersionNumber + 1),
                    BaseUrl = definition.BaseUrl,
                    Model = definition.Model,
                    Backend = definition.Backend,
                    ProtectedCredential = definition.ClearCredential
                        ? null
                        : definition.Credential is null
                            ? currentVersion.ProtectedCredential
                            : secretProtector.Protect(definition.Credential),
                    CreatedAtUtc = nowUtc,
                });
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(ProviderConfigMutationStatus.Succeeded, await GetCurrentAsync(id, cancellationToken));
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                return new(ProviderConfigMutationStatus.Conflict);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                return new(ProviderConfigMutationStatus.Conflict);
            }
        });
    }

    private async Task ClearDefaultAsync(Guid? exceptId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await dbContext.ProviderConfigs
            .Where(config => config.DefaultMarker == DefaultMarker && (!exceptId.HasValue || config.Id != exceptId.Value))
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(config => config.DefaultMarker, (string?)null)
                    .SetProperty(config => config.UpdatedAtUtc, nowUtc)
                    .SetProperty(config => config.ConcurrencyVersion, config => config.ConcurrencyVersion + 1),
                cancellationToken);
    }

    private Task<ProviderConfigRecord> GetCurrentAsync(Guid id, CancellationToken cancellationToken) =>
        CurrentConfigs(id).SingleAsync(cancellationToken);

    private IQueryable<ProviderConfigRecord> CurrentConfigs(
        Guid? id = null,
        bool orderByName = false)
    {
        var configs = dbContext.ProviderConfigs.AsNoTracking();
        if (id.HasValue)
        {
            configs = configs.Where(config => config.Id == id.Value);
        }

        if (orderByName)
        {
            configs = configs.OrderBy(config => config.Name);
        }

        return from config in configs
               join version in dbContext.ProviderConfigVersions.AsNoTracking()
                   on config.CurrentVersionId equals version.Id
               select new ProviderConfigRecord(
                   config.Id,
                   config.Name,
                   config.ProviderType,
                   config.IsEnabled,
                   config.DefaultMarker == DefaultMarker,
                   version.Id,
                   version.VersionNumber,
                   version.BaseUrl,
                   version.Model,
                   version.Backend,
                   version.ProtectedCredential != null,
                   config.CreatedAtUtc,
                   config.UpdatedAtUtc);
    }
}
