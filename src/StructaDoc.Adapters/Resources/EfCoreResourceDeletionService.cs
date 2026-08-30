using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Resources;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Adapters.Resources;

public sealed class EfCoreResourceDeletionService(StructaDocDbContext dbContext) : IResourceDeletionService
{
    private static readonly string[] FinalStatuses = ParseRunStatuses.Final;
    private const int DeletionAttempts = 3;

    public async Task<ResourceDeletionResult> RequestDocumentDeletionAsync(Guid documentId, ResourceAccessContext access, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < DeletionAttempts; attempt++)
        {
            var document = await ApplyAccess(
                    dbContext.Documents
                        .Include(item => item.ParseRuns)
                        .Where(item => item.Id == documentId),
                    access,
                    DocumentPermissions.Delete)
                .SingleOrDefaultAsync(cancellationToken);
            if (document is null)
            {
                return new(ResourceDeletionStatus.NotFound);
            }

            if (document.LifecycleState != ResourceLifecycleStates.Active)
            {
                return new(
                    ResourceDeletionStatus.AlreadyPending,
                    await ExistingJobIdAsync(
                        CleanupTargetTypes.Document,
                        documentId,
                        cancellationToken));
            }

            if (document.ParseRuns.Any(run => !FinalStatuses.Contains(run.Status)))
            {
                return new(ResourceDeletionStatus.ActiveParseRuns);
            }

            var refs = (await StorageRefsForRunsAsync(
                    dbContext.ParseRuns.Where(run => run.DocumentId == documentId),
                    cancellationToken))
                .Append(document.StorageRef)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            document.LifecycleState = ResourceLifecycleStates.DeletionPending;
            document.DeletionRequestedAtUtc = nowUtc;
            foreach (var run in document.ParseRuns)
            {
                run.LifecycleState = ResourceLifecycleStates.DeletionPending;
                run.DeletionRequestedAtUtc = nowUtc;
            }

            var job = NewJob(CleanupTargetTypes.Document, documentId, refs, nowUtc);
            dbContext.CleanupJobs.Add(job);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return new(ResourceDeletionStatus.Accepted, job.Id);
            }
            catch (DbUpdateConcurrencyException) when (attempt + 1 < DeletionAttempts)
            {
                // Parse Run creation increments the same Document concurrency version in its
                // transaction. Reloading makes the next pass observe that new active run.
                dbContext.ChangeTracker.Clear();
            }
        }

        throw new DbUpdateConcurrencyException(
            $"Document '{documentId:D}' kept changing while deletion was requested.");
    }

    public async Task<ResourceDeletionResult> RequestParseRunDeletionAsync(Guid parseRunId, ResourceAccessContext access, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var run = await ApplyRunAccess(dbContext.ParseRuns.Where(item => item.Id == parseRunId), access, DocumentPermissions.Delete).SingleOrDefaultAsync(cancellationToken);
        if (run is null) return new(ResourceDeletionStatus.NotFound);
        if (run.LifecycleState != ResourceLifecycleStates.Active) return new(ResourceDeletionStatus.AlreadyPending, await ExistingJobIdAsync(CleanupTargetTypes.ParseRun, parseRunId, cancellationToken));
        if (!FinalStatuses.Contains(run.Status)) return new(ResourceDeletionStatus.ActiveParseRuns);
        var refs = (await StorageRefsForRunsAsync(
                dbContext.ParseRuns.Where(item => item.Id == parseRunId),
                cancellationToken))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        run.LifecycleState = ResourceLifecycleStates.DeletionPending;
        run.DeletionRequestedAtUtc = nowUtc;
        var job = NewJob(CleanupTargetTypes.ParseRun, parseRunId, refs, nowUtc);
        dbContext.CleanupJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ResourceDeletionStatus.Accepted, job.Id);
    }

    private Task<Guid?> ExistingJobIdAsync(string type, Guid id, CancellationToken cancellationToken) => dbContext.CleanupJobs.AsNoTracking().Where(job => job.TargetType == type && job.TargetId == id).Select(job => (Guid?)job.Id).SingleOrDefaultAsync(cancellationToken);
    private async Task<IReadOnlyList<string>> StorageRefsForRunsAsync(
        IQueryable<ParseRunEntity> runs,
        CancellationToken cancellationToken)
    {
        var runRows = await runs
            .AsNoTracking()
            .OrderBy(run => run.Id)
            .Select(run => new RunStorageRow(run.Id, run.ConversionJson))
            .ToArrayAsync(cancellationToken);
        var assetRows = await runs
            .AsNoTracking()
            .SelectMany(run => run.Assets)
            .OrderBy(asset => asset.ParseRunId)
            .ThenBy(asset => asset.Id)
            .Select(asset => new StorageRefRow(
                asset.ParseRunId,
                asset.Id,
                asset.StorageRef))
            .ToArrayAsync(cancellationToken);
        var artifactRows = await runs
            .AsNoTracking()
            .SelectMany(run => run.Artifacts)
            .OrderBy(artifact => artifact.ParseRunId)
            .ThenBy(artifact => artifact.Id)
            .Select(artifact => new StorageRefRow(
                artifact.ParseRunId,
                artifact.Id,
                artifact.StorageRef))
            .ToArrayAsync(cancellationToken);
        var segmentRows = await runs
            .AsNoTracking()
            .SelectMany(run => run.Segments)
            .OrderBy(segment => segment.ParseRunId)
            .ThenBy(segment => segment.Id)
            .Select(segment => new StorageRefRow(
                segment.ParseRunId,
                segment.Id,
                segment.StorageRef))
            .ToArrayAsync(cancellationToken);

        var assetsByRun = assetRows.ToLookup(row => row.ParseRunId);
        var artifactsByRun = artifactRows.ToLookup(row => row.ParseRunId);
        var segmentsByRun = segmentRows.ToLookup(row => row.ParseRunId);
        var storageRefs = new List<string>(
            runRows.Length
            + assetRows.Length
            + artifactRows.Length
            + (segmentRows.Length * 2));

        foreach (var run in runRows)
        {
            storageRefs.AddRange(assetsByRun[run.Id].Select(row => row.StorageRef));
            storageRefs.AddRange(artifactsByRun[run.Id].Select(row => row.StorageRef));
            storageRefs.Add($"parse-runs/{run.Id:N}/provider/result.zip");
            foreach (var segment in segmentsByRun[run.Id])
            {
                storageRefs.Add(segment.StorageRef);
                storageRefs.Add($"parse-runs/{segment.Id:N}/provider/result.zip");
            }

            if (run.ConversionJson is not null)
            {
                ParseRunConversion conversion;
                try
                {
                    conversion = ParseRunConversion.FromJson(run.ConversionJson);
                }
                catch (JsonException)
                {
                    throw new InvalidDataException(
                        $"The persisted conversion snapshot for Parse Run '{run.Id:D}' is invalid. "
                        + "Restore or repair this record before requesting deletion again.");
                }

                storageRefs.Add(conversion.StorageRef);
            }
        }

        return storageRefs;
    }

    private sealed record RunStorageRow(Guid Id, string? ConversionJson);

    private sealed record StorageRefRow(Guid ParseRunId, Guid Id, string StorageRef);

    private static CleanupJobEntity NewJob(string type, Guid id, IReadOnlyList<string> refs, DateTime now) => new() { Id = Guid.NewGuid(), TargetType = type, TargetId = id, StorageRefsJson = JsonSerializer.Serialize(refs), Status = CleanupJobStatuses.Pending, NextAttemptAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };

    private static IQueryable<DocumentEntity> ApplyAccess(IQueryable<DocumentEntity> query, ResourceAccessContext access, DocumentPermissions permission) => access.IsAdministrator ? query : access.HasPrincipalIdentity ? query.Where(document => document.OwnerIssuer == access.Issuer && document.OwnerSubject == access.Subject || document.AccessGrants.Any(grant => grant.PrincipalIssuer == access.Issuer && grant.PrincipalSubject == access.Subject && (grant.Permissions & (int)permission) == (int)permission)) : query.Where(_ => false);
    private static IQueryable<ParseRunEntity> ApplyRunAccess(IQueryable<ParseRunEntity> query, ResourceAccessContext access, DocumentPermissions permission) => access.IsAdministrator ? query : access.HasPrincipalIdentity ? query.Where(run => run.Document.OwnerIssuer == access.Issuer && run.Document.OwnerSubject == access.Subject || run.Document.AccessGrants.Any(grant => grant.PrincipalIssuer == access.Issuer && grant.PrincipalSubject == access.Subject && (grant.Permissions & (int)permission) == (int)permission)) : query.Where(_ => false);
}
