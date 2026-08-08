using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.ParseRuns;

public sealed class EfCoreParseRunService(StructaDocDbContext dbContext) : IParseRunService
{
    private const string DefaultMarker = "default";
    private const int CancellationAttempts = 3;

    public async Task<ParseRunCreationResult> CreateAsync(
        ParseRunCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.IdempotencyKey is not null)
        {
            var replay = await FindReplayAsync(request, cancellationToken);
            if (replay is not null)
            {
                return new(ParseRunCreationStatus.Replayed, replay);
            }
        }

        var document = await dbContext.Documents.AsNoTracking()
            .Where(item => item.Id == request.DocumentId)
            .Select(item => new { item.Id, item.MediaType })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return new(ParseRunCreationStatus.DocumentNotFound);
        }

        var configQuery = dbContext.ProviderConfigs.AsNoTracking();
        var config = request.ProviderConfigId.HasValue
            ? await configQuery.SingleOrDefaultAsync(item => item.Id == request.ProviderConfigId.Value, cancellationToken)
            : await configQuery.SingleOrDefaultAsync(item => item.DefaultMarker == DefaultMarker, cancellationToken);
        if (config is null)
        {
            return new(request.ProviderConfigId.HasValue
                ? ParseRunCreationStatus.ProviderConfigNotFound
                : ParseRunCreationStatus.ProviderUnavailable);
        }

        if (!config.IsEnabled)
        {
            return new(ParseRunCreationStatus.ProviderUnavailable);
        }

        var versionExists = await dbContext.ProviderConfigVersions.AsNoTracking()
            .AnyAsync(version => version.Id == config.CurrentVersionId, cancellationToken);
        if (!versionExists)
        {
            return new(ParseRunCreationStatus.ProviderUnavailable);
        }

        var entity = new ParseRunEntity
        {
            Id = Guid.NewGuid(),
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
            CreatedBy = request.CreatedBy,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAtUtc = request.CreatedAtUtc,
        };

        try
        {
            dbContext.ParseRuns.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(ParseRunCreationStatus.Created, ToRecord(entity));
        }
        catch (DbUpdateException) when (request.IdempotencyKey is not null)
        {
            dbContext.ChangeTracker.Clear();
            var replay = await FindReplayAsync(request, cancellationToken);
            return replay is null
                ? new(ParseRunCreationStatus.Conflict)
                : new(ParseRunCreationStatus.Replayed, replay);
        }
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
        CancellationToken cancellationToken)
    {
        var id = await dbContext.ParseRuns.AsNoTracking()
            .Where(entity => entity.DocumentId == request.DocumentId
                && entity.CreatedBy == request.CreatedBy
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
