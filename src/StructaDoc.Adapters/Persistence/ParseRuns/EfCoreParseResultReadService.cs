using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Adapters.Persistence.ParseRuns;

public sealed class EfCoreParseResultReadService(
    StructaDocDbContext dbContext,
    IFileStorage fileStorage,
    ILogger<EfCoreParseResultReadService> logger) : IParseResultReadService
{
    public async Task<IReadOnlyList<ParseRunRecord>> ListForDocumentAsync(Guid documentId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadDocumentAsync(documentId, access, cancellationToken)) return [];
        // Narrowed and ordered on the entity rather than on the projection. A database cannot filter
        // by a property of a record it never constructs, so the same conditions placed after the
        // Select below fail to translate and the endpoint answers 500.
        return await QueryRuns(forDocumentId: documentId).ToListAsync(cancellationToken);
    }

    public async Task<ParseRunRecord?> GetAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        return await QueryRuns(parseRunId).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParsePageRecord>?> ListPagesAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        return await dbContext.ParsePages.AsNoTracking().Where(page => page.ParseRunId == parseRunId).OrderBy(page => page.Number)
            .Select(page => new ParsePageRecord(page.Number, page.Width, page.Height, page.Unit)).ToListAsync(cancellationToken);
    }

    public async Task<ParseBlockPage?> ListBlocksAsync(Guid parseRunId, ResourceAccessContext access, int limit, int? afterSequence = null, int? pageNumber = null, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        var query = dbContext.ParseBlocks.AsNoTracking().Where(block => block.ParseRunId == parseRunId);
        if (afterSequence.HasValue) query = query.Where(block => block.Sequence > afterSequence.Value);
        if (pageNumber.HasValue) query = query.Where(block => block.PageNumber == pageNumber.Value);
        var rows = await query.OrderBy(block => block.Sequence).Take(limit + 1).Select(block => new ParseBlockRecord(
            block.Id, block.Sequence, block.PageNumber, block.Type, block.Subtype, block.Content, block.ContentFormat,
            block.BoundingBoxX0.HasValue && block.BoundingBoxY0.HasValue && block.BoundingBoxX1.HasValue && block.BoundingBoxY1.HasValue
                ? new BoundingBoxRecord(block.BoundingBoxX0.Value, block.BoundingBoxY0.Value, block.BoundingBoxX1.Value, block.BoundingBoxY1.Value)
                : null,
            block.Confidence, block.AssetId)).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new ParseBlockPage(rows, hasMore ? rows[^1].Sequence : null);
    }

    public async Task<IReadOnlyList<ParseAssetRecord>?> ListAssetsAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        return await dbContext.ParseAssets.AsNoTracking().Where(asset => asset.ParseRunId == parseRunId).OrderBy(asset => asset.Name)
            .Select(asset => new ParseAssetRecord(asset.Id, asset.Name, asset.MediaType, asset.SizeBytes, asset.Sha256, asset.Width, asset.Height)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParseExportAssetRecord>?> ListAssetsForExportAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        return await dbContext.ParseAssets.AsNoTracking().Where(asset => asset.ParseRunId == parseRunId).OrderBy(asset => asset.Name)
            .Select(asset => new ParseExportAssetRecord(asset.ParseRunId, asset.Id, asset.Name, asset.MediaType, asset.SizeBytes, asset.Sha256, asset.Width, asset.Height, asset.StorageRef)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParseArtifactRecord>?> ListArtifactsAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        return await dbContext.ParseArtifacts.AsNoTracking().Where(artifact => artifact.ParseRunId == parseRunId).OrderBy(artifact => artifact.Type).ThenBy(artifact => artifact.Name)
            .Select(artifact => new ParseArtifactRecord(artifact.Id, artifact.Type, artifact.Name, artifact.MediaType, artifact.SizeBytes, artifact.Sha256, artifact.CreatedAtUtc)).ToListAsync(cancellationToken);
    }

    public async Task<ParseResultContent?> OpenAssetAsync(Guid parseRunId, Guid assetId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        var item = await dbContext.ParseAssets.AsNoTracking().Where(asset => asset.ParseRunId == parseRunId && asset.Id == assetId)
            .Select(asset => new StoredResult(asset.Name, asset.MediaType, asset.SizeBytes, asset.Sha256, asset.StorageRef)).SingleOrDefaultAsync(cancellationToken);
        return await OpenAsync(item, parseRunId, cancellationToken);
    }

    public Task<ParseResultContent?> OpenExportAssetAsync(Guid parseRunId, ParseExportAssetRecord asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.ParseRunId != parseRunId)
        {
            throw new ArgumentException("The export Asset does not belong to the requested Parse Run.", nameof(asset));
        }

        return OpenAsync(
            new StoredResult(asset.Name, asset.MediaType, asset.SizeBytes, asset.Sha256, asset.StorageRef),
            parseRunId,
            cancellationToken);
    }

    public async Task<ParseResultContent?> OpenArtifactAsync(Guid parseRunId, Guid artifactId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        var item = await dbContext.ParseArtifacts.AsNoTracking().Where(artifact => artifact.ParseRunId == parseRunId && artifact.Id == artifactId)
            .Select(artifact => new StoredResult(artifact.Name, artifact.MediaType, artifact.SizeBytes, artifact.Sha256, artifact.StorageRef)).SingleOrDefaultAsync(cancellationToken);
        return await OpenAsync(item, parseRunId, cancellationToken);
    }

    public async Task<ParseResultContent?> OpenMarkdownAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(parseRunId, access, cancellationToken)) return null;
        var item = await dbContext.ParseArtifacts.AsNoTracking().Where(artifact => artifact.ParseRunId == parseRunId && artifact.Type == ArtifactTypes.Markdown)
            .OrderBy(artifact => artifact.Name).Select(artifact => new StoredResult(artifact.Name, artifact.MediaType, artifact.SizeBytes, artifact.Sha256, artifact.StorageRef)).FirstOrDefaultAsync(cancellationToken);
        return await OpenAsync(item, parseRunId, cancellationToken);
    }

    private async Task<ParseResultContent?> OpenAsync(StoredResult? item, Guid parseRunId, CancellationToken cancellationToken)
    {
        if (item is null) return null;
        try { return new ParseResultContent(await fileStorage.OpenReadAsync(item.StorageRef, cancellationToken), item.Name, item.MediaType, item.SizeBytes, item.Sha256); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogError(exception, "Stored result for Parse Run {ParseRunId} is missing.", parseRunId);
            throw new ParseResultContentUnavailableException(parseRunId, exception);
        }
    }

    private Task<bool> CanReadRunAsync(Guid parseRunId, ResourceAccessContext access, CancellationToken cancellationToken) =>
        ApplyAccess(dbContext.ParseRuns.AsNoTracking().Where(run => run.Id == parseRunId && run.LifecycleState == ResourceLifecycleStates.Active), access)
            .AnyAsync(cancellationToken);

    private Task<bool> CanReadDocumentAsync(Guid documentId, ResourceAccessContext access, CancellationToken cancellationToken) =>
        ApplyDocumentAccess(dbContext.Documents.AsNoTracking().Where(document => document.Id == documentId && document.LifecycleState == ResourceLifecycleStates.Active), access)
            .AnyAsync(cancellationToken);

    private IQueryable<ParseRunEntity> ApplyAccess(IQueryable<ParseRunEntity> query, ResourceAccessContext access) =>
        access.IsAdministrator ? query : access.HasPrincipalIdentity
            ? ApplyPrincipalAccess(
                query,
                access,
                DocumentOwnerIdentity.From(access),
                DocumentOwnerIdentity.CanCompareTextGrant(access, dbContext.Database.ProviderName))
            : query.Where(_ => false);

    private IQueryable<DocumentEntity> ApplyDocumentAccess(IQueryable<DocumentEntity> query, ResourceAccessContext access) =>
        access.IsAdministrator ? query : access.HasPrincipalIdentity
            ? ApplyPrincipalDocumentAccess(
                query,
                access,
                DocumentOwnerIdentity.From(access),
                DocumentOwnerIdentity.CanCompareTextGrant(access, dbContext.Database.ProviderName))
            : query.Where(_ => false);

    private static IQueryable<ParseRunEntity> ApplyPrincipalAccess(
        IQueryable<ParseRunEntity> query,
        ResourceAccessContext access,
        DocumentOwnerIdentity owner,
        bool canCompareTextGrant) => !canCompareTextGrant
        ? query.Where(run =>
            run.Document.OwnerIssuer == owner.Issuer
            && run.Document.OwnerSubject == owner.Subject)
        : query.Where(run =>
            run.Document.OwnerIssuer == owner.Issuer
            && run.Document.OwnerSubject == owner.Subject
            || run.Document.AccessGrants.Any(grant =>
                grant.PrincipalIssuer == access.Issuer
                && grant.PrincipalSubject == access.Subject
                && (grant.Permissions & (int)DocumentPermissions.Read) != 0));

    private static IQueryable<DocumentEntity> ApplyPrincipalDocumentAccess(
        IQueryable<DocumentEntity> query,
        ResourceAccessContext access,
        DocumentOwnerIdentity owner,
        bool canCompareTextGrant) => !canCompareTextGrant
        ? query.Where(document =>
            document.OwnerIssuer == owner.Issuer
            && document.OwnerSubject == owner.Subject)
        : query.Where(document =>
            document.OwnerIssuer == owner.Issuer
            && document.OwnerSubject == owner.Subject
            || document.AccessGrants.Any(grant =>
                grant.PrincipalIssuer == access.Issuer
                && grant.PrincipalSubject == access.Subject
                && (grant.Permissions & (int)DocumentPermissions.Read) != 0));

    private IQueryable<ParseRunRecord> QueryRuns(Guid? id = null, Guid? forDocumentId = null)
    {
        var query = dbContext.ParseRuns.AsNoTracking().Where(run => run.LifecycleState == ResourceLifecycleStates.Active);
        if (id.HasValue) query = query.Where(run => run.Id == id.Value);
        // Newest first, then by ID: two Parse Runs created in the same tick would otherwise come back
        // in whatever order the database chose that time.
        if (forDocumentId.HasValue) query = query.Where(run => run.DocumentId == forDocumentId.Value).OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.Id);
        return query.Select(run => new ParseRunRecord(run.Id, run.DocumentId, run.Status, run.Stage, run.ProviderType, run.ProviderConfigId, run.ProviderConfigVersion, run.OptionsJson, run.SourceMediaType, run.SubmittedMediaType, run.AttemptCount, run.MaxAttempts, run.NextAttemptAtUtc, run.ErrorCode, run.ErrorMessage, run.CreatedAtUtc, run.StartedAtUtc, run.CompletedAtUtc));
    }

    private sealed record StoredResult(string Name, string MediaType, long SizeBytes, string Sha256, string StorageRef);
}

public sealed class ParseResultContentUnavailableException(Guid parseRunId, Exception innerException)
    : IOException($"Stored result content for Parse Run '{parseRunId:D}' is unavailable.", innerException);
