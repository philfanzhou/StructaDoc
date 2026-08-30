using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Providers;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Adapters.Persistence.Providers;

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

    /// <summary>
    /// Removes a Provider configuration and every version of it, but only while nothing points at
    /// one. A running Parse Run reads its configuration version as it executes, and a finished one
    /// keeps it as the record of how its result was produced; neither survives the rows going away.
    /// Disabling is the way to retire a configuration that has been used.
    /// </summary>
    public async Task<ProviderConfigDeletionStatus> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync<ProviderConfigDeletionStatus>(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var config = await dbContext.ProviderConfigs.SingleOrDefaultAsync(
                    item => item.Id == id,
                    cancellationToken);
                if (config is null)
                {
                    return ProviderConfigDeletionStatus.NotFound;
                }

                // Read inside the transaction rather than before it. A Parse Run created while an
                // administrator is deciding would otherwise be left pointing at rows that are gone,
                // and nothing in the schema would stop it: a Parse Run records its Provider
                // configuration by ID rather than through a foreign key.
                if (await dbContext.ParseRuns.AnyAsync(
                        run => run.ProviderConfigId == id
                            && !ParseRunStatuses.Final.Contains(run.Status),
                        cancellationToken))
                {
                    return ProviderConfigDeletionStatus.ReferencedByActiveParseRun;
                }

                if (await dbContext.ParseRuns.AnyAsync(
                        run => run.ProviderConfigId == id,
                        cancellationToken))
                {
                    return ProviderConfigDeletionStatus.ReferencedByParseHistory;
                }

                // Versions go first: their foreign key back to the configuration restricts deletion,
                // so removing the parent while any of them remain is refused by the database.
                await dbContext.ProviderConfigVersions
                    .Where(version => version.ProviderConfigId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                dbContext.ProviderConfigs.Remove(config);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ProviderConfigDeletionStatus.Deleted;
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();

                // The concurrency token stops a configuration that changed under the delete, and the
                // version foreign key stops one a concurrent write attached a row to.
                return ProviderConfigDeletionStatus.ReferencedByActiveParseRun;
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
