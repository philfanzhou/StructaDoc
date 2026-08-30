using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Adapters.Persistence.ParseRuns;

public sealed class EfCoreParseRunService(StructaDocDbContext dbContext) : IParseRunService
{
    private const string DefaultMarker = "default";
    private const int CancellationAttempts = 3;

    public async Task<ParseRunCreationResult> CreateAsync(
        ParseRunCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Actor);

        var actorIssuer = request.Actor.EncodeIssuer();
        var actorSubject = request.Actor.EncodeSubject();

        if (request.IdempotencyKey is not null)
        {
            var replay = await FindReplayAsync(
                request,
                actorIssuer,
                actorSubject,
                cancellationToken);
            if (replay is not null)
            {
                return new(ParseRunCreationStatus.Replayed, replay);
            }
        }

        var parseRunId = Guid.NewGuid();
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            // A retry can mean the transaction committed but its acknowledgement was lost. The
            // operation ID is stable across every invocation of this delegate, so durable state
            // settles that unknown outcome before any concurrency guards are advanced again.
            var committedRun = await GetAsync(parseRunId, cancellationToken);
            if (committedRun is not null)
            {
                return new ParseRunCreationResult(
                    ParseRunCreationStatus.Created,
                    committedRun);
            }

            // A failed attempt can leave its uncommitted entity tracked even though the database
            // transaction rolled back. Detach only this operation's entity before rebuilding it.
            var trackedRun = dbContext.ChangeTracker.Entries<ParseRunEntity>()
                .SingleOrDefault(entry => entry.Entity.Id == parseRunId);
            if (trackedRun is not null)
            {
                trackedRun.State = EntityState.Detached;
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                cancellationToken);

            var document = await dbContext.Documents.AsNoTracking()
                .Where(item => item.Id == request.DocumentId
                    && item.LifecycleState == ResourceLifecycleStates.Active)
                .Select(item => new { item.Id, item.MediaType, item.ConcurrencyVersion })
                .SingleOrDefaultAsync(cancellationToken);
            if (document is null)
            {
                return new ParseRunCreationResult(ParseRunCreationStatus.DocumentNotFound);
            }

            var configQuery = dbContext.ProviderConfigs.AsNoTracking();
            var config = request.ProviderConfigId.HasValue
                ? await configQuery.SingleOrDefaultAsync(
                    item => item.Id == request.ProviderConfigId.Value,
                    cancellationToken)
                : await configQuery.SingleOrDefaultAsync(
                    item => item.DefaultMarker == DefaultMarker,
                    cancellationToken);
            if (config is null)
            {
                return new ParseRunCreationResult(request.ProviderConfigId.HasValue
                    ? ParseRunCreationStatus.ProviderConfigNotFound
                    : ParseRunCreationStatus.ProviderUnavailable);
            }

            if (!config.IsEnabled)
            {
                return new ParseRunCreationResult(ParseRunCreationStatus.ProviderUnavailable);
            }

            var version = await dbContext.ProviderConfigVersions.AsNoTracking()
                .Where(item => item.Id == config.CurrentVersionId
                    && item.ProviderConfigId == config.Id)
                .Select(item => new { HasCredential = item.ProtectedCredential != null })
                .SingleOrDefaultAsync(cancellationToken);
            if (version is null)
            {
                return new ParseRunCreationResult(ParseRunCreationStatus.ProviderUnavailable);
            }

            if (!version.HasCredential
                && ProviderTypeDescriptors.RequiresCredential(config.ProviderType))
            {
                return new ParseRunCreationResult(ParseRunCreationStatus.ProviderCredentialMissing);
            }

            // These conditional version increments are the cross-database lock shared with both
            // deletion paths. They and the Parse Run insert commit as one unit.
            var documentGuard = await dbContext.Documents
                .Where(item => item.Id == document.Id
                    && item.LifecycleState == ResourceLifecycleStates.Active
                    && item.ConcurrencyVersion == document.ConcurrencyVersion)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(
                        item => item.ConcurrencyVersion,
                        item => item.ConcurrencyVersion + 1),
                    cancellationToken);
            if (documentGuard != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                var replay = request.IdempotencyKey is null
                    ? null
                    : await FindCanonicalReplayAsync(
                        request,
                        actorIssuer,
                        actorSubject,
                        cancellationToken);
                return replay is null
                    ? new ParseRunCreationResult(ParseRunCreationStatus.DocumentNotFound)
                    : new ParseRunCreationResult(ParseRunCreationStatus.Replayed, replay);
            }

            var providerGuard = await dbContext.ProviderConfigs
                .Where(item => item.Id == config.Id
                    && item.IsEnabled
                    && item.CurrentVersionId == config.CurrentVersionId
                    && item.ConcurrencyVersion == config.ConcurrencyVersion)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(
                        item => item.ConcurrencyVersion,
                        item => item.ConcurrencyVersion + 1),
                    cancellationToken);
            if (providerGuard != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                var replay = request.IdempotencyKey is null
                    ? null
                    : await FindCanonicalReplayAsync(
                        request,
                        actorIssuer,
                        actorSubject,
                        cancellationToken);
                return replay is null
                    ? new ParseRunCreationResult(ParseRunCreationStatus.ProviderUnavailable)
                    : new ParseRunCreationResult(ParseRunCreationStatus.Replayed, replay);
            }

            var entity = new ParseRunEntity
            {
                Id = parseRunId,
                DocumentId = document.Id,
                Status = ParseRunStatuses.Queued,
                ProviderType = config.ProviderType,
                ProviderConfigId = config.Id,
                ProviderConfigVersion = config.CurrentVersionId,
                OptionsJson = request.OptionsJson,
                SourceMediaType = document.MediaType,
                SubmittedMediaType = document.MediaType,
                AttemptCount = 0,
                MaxAttempts = request.MaxAttempts,
                NextAttemptAtUtc = request.CreatedAtUtc,
                CreatedByIssuer = actorIssuer,
                CreatedBySubject = actorSubject,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAtUtc = request.CreatedAtUtc,
            };

            try
            {
                dbContext.ParseRuns.Add(entity);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ParseRunCreationResult(
                    ParseRunCreationStatus.Created,
                    ToRecord(entity));
            }
            catch (DbUpdateException) when (request.IdempotencyKey is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                var replay = await FindCanonicalReplayAsync(
                    request,
                    actorIssuer,
                    actorSubject,
                    cancellationToken);
                return replay is null
                    ? new ParseRunCreationResult(ParseRunCreationStatus.Conflict)
                    : new ParseRunCreationResult(ParseRunCreationStatus.Replayed, replay);
            }
        });
    }

    public Task<ParseRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        QueryRecords(id).SingleOrDefaultAsync(cancellationToken);

    public async Task<ParseRunCancellationResult> RequestCancellationAsync(
        Guid id,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Parse Run timestamps must use UTC.", nameof(nowUtc));
        }

        // One conditional update decides the race against a Worker transition: whichever statement
        // commits first wins, and a run that already reached a final state is never reopened. The
        // lease is deliberately left in place so the owning Worker can observe the request and
        // finalize without waiting for its lease to lapse.
        var cancellable = ParseRunStatuses.Cancellable;

        for (var attempt = 0; attempt < CancellationAttempts; attempt++)
        {
            var affectedRows = await dbContext.ParseRuns
                .Where(entity => entity.Id == id && cancellable.Contains(entity.Status))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(entity => entity.Status, ParseRunStatuses.CancelRequested)
                        .SetProperty(
                            entity => entity.ConcurrencyVersion,
                            entity => entity.ConcurrencyVersion + 1),
                    cancellationToken);

            var parseRun = await GetAsync(id, cancellationToken);
            if (affectedRows == 1)
            {
                return new(ParseRunCancellationStatus.Requested, parseRun);
            }

            switch (parseRun)
            {
                case null:
                    return new(ParseRunCancellationStatus.NotFound);
                case { Status: ParseRunStatuses.CancelRequested }:
                    return new(ParseRunCancellationStatus.AlreadyRequested, parseRun);
                case not null when ParseRunStatuses.IsFinal(parseRun.Status):
                    return new(ParseRunCancellationStatus.AlreadyFinal, parseRun);
            }

            // The run re-entered a cancellable state between the update and the read, so the
            // request has not taken effect yet. Retry rather than report a misleading outcome.
        }

        return new(ParseRunCancellationStatus.Conflict, await GetAsync(id, cancellationToken));
    }

    private async Task<ParseRunRecord?> FindReplayAsync(
        ParseRunCreateRequest request,
        byte[] actorIssuer,
        byte[] actorSubject,
        CancellationToken cancellationToken)
    {
        var canonicalReplay = await FindCanonicalReplayAsync(
            request,
            actorIssuer,
            actorSubject,
            cancellationToken);
        if (canonicalReplay is not null)
        {
            return canonicalReplay;
        }

        var legacyActor = CanonicalActorPersistence.EncodeLegacy(
            request.Actor.ToLegacyDisplayString(),
            CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount);
        var legacyCandidates = await dbContext.ParseRuns.AsNoTracking()
            .Where(entity => entity.DocumentId == request.DocumentId
                && entity.IdempotencyKey == request.IdempotencyKey
                && entity.CreatedByIssuer == null
                && entity.CreatedBySubject == null
                && entity.CreatedByLegacy != null)
            .Select(entity => new { entity.Id, entity.CreatedByLegacy })
            .ToListAsync(cancellationToken);
        var legacyId = legacyCandidates
            .SingleOrDefault(candidate => candidate.CreatedByLegacy!.AsSpan().SequenceEqual(legacyActor))
            ?.Id;
        return legacyId.HasValue ? await GetAsync(legacyId.Value, cancellationToken) : null;
    }

    private async Task<ParseRunRecord?> FindCanonicalReplayAsync(
        ParseRunCreateRequest request,
        byte[] actorIssuer,
        byte[] actorSubject,
        CancellationToken cancellationToken)
    {
        var id = await dbContext.ParseRuns.AsNoTracking()
            .Where(entity => entity.DocumentId == request.DocumentId
                && entity.CreatedByIssuer == actorIssuer
                && entity.CreatedBySubject == actorSubject
                && entity.IdempotencyKey == request.IdempotencyKey)
            .Select(entity => (Guid?)entity.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return id.HasValue ? await GetAsync(id.Value, cancellationToken) : null;
    }

    private IQueryable<ParseRunRecord> QueryRecords(Guid? id = null)
    {
        var parseRuns = dbContext.ParseRuns.AsNoTracking();
        if (id.HasValue)
        {
            parseRuns = parseRuns.Where(entity => entity.Id == id.Value);
        }

        return parseRuns.Select(entity => new ParseRunRecord(
            entity.Id,
            entity.DocumentId,
            entity.Status,
            entity.Stage,
            entity.ProviderType,
            entity.ProviderConfigId,
            entity.ProviderConfigVersion,
            entity.OptionsJson,
            entity.SourceMediaType,
            entity.SubmittedMediaType,
            entity.AttemptCount,
            entity.MaxAttempts,
            entity.NextAttemptAtUtc,
            entity.ErrorCode,
            entity.ErrorMessage,
            entity.CreatedAtUtc,
            entity.StartedAtUtc,
            entity.CompletedAtUtc));
    }

    private static ParseRunRecord ToRecord(ParseRunEntity entity) => new(
        entity.Id,
        entity.DocumentId,
        entity.Status,
        entity.Stage,
        entity.ProviderType,
        entity.ProviderConfigId,
        entity.ProviderConfigVersion,
        entity.OptionsJson,
        entity.SourceMediaType,
        entity.SubmittedMediaType,
        entity.AttemptCount,
        entity.MaxAttempts,
        entity.NextAttemptAtUtc,
        entity.ErrorCode,
        entity.ErrorMessage,
        entity.CreatedAtUtc,
        entity.StartedAtUtc,
        entity.CompletedAtUtc);
}
