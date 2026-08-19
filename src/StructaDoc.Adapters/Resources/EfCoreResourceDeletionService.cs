using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Resources;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;

namespace StructaDoc.Adapters.Resources;

public sealed class EfCoreResourceDeletionService(StructaDocDbContext dbContext) : IResourceDeletionService
{
    private static readonly string[] FinalStatuses = ParseRunStatuses.Final;

    public async Task<ResourceDeletionResult> RequestDocumentDeletionAsync(Guid documentId, ResourceAccessContext access, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var document = await ApplyAccess(dbContext.Documents.Include(item => item.ParseRuns).ThenInclude(run => run.Assets).Include(item => item.ParseRuns).ThenInclude(run => run.Artifacts).Include(item => item.ParseRuns).ThenInclude(run => run.Segments).Where(item => item.Id == documentId), access, DocumentPermissions.Delete).SingleOrDefaultAsync(cancellationToken);
        if (document is null) return new(ResourceDeletionStatus.NotFound);
        if (document.LifecycleState != ResourceLifecycleStates.Active) return new(ResourceDeletionStatus.AlreadyPending, await ExistingJobIdAsync(CleanupTargetTypes.Document, documentId, cancellationToken));
        if (document.ParseRuns.Any(run => !FinalStatuses.Contains(run.Status))) return new(ResourceDeletionStatus.ActiveParseRuns);

        var refs = document.ParseRuns.SelectMany(StorageRefsForRun).Append(document.StorageRef).Distinct(StringComparer.Ordinal).ToArray();
        document.LifecycleState = ResourceLifecycleStates.DeletionPending;
        document.DeletionRequestedAtUtc = nowUtc;
        foreach (var run in document.ParseRuns) { run.LifecycleState = ResourceLifecycleStates.DeletionPending; run.DeletionRequestedAtUtc = nowUtc; }
        var job = NewJob(CleanupTargetTypes.Document, documentId, refs, nowUtc);
        dbContext.CleanupJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ResourceDeletionStatus.Accepted, job.Id);
    }

    public async Task<ResourceDeletionResult> RequestParseRunDeletionAsync(Guid parseRunId, ResourceAccessContext access, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var run = await ApplyRunAccess(dbContext.ParseRuns.Include(item => item.Assets).Include(item => item.Artifacts).Include(item => item.Segments).Where(item => item.Id == parseRunId), access, DocumentPermissions.Delete).SingleOrDefaultAsync(cancellationToken);
        if (run is null) return new(ResourceDeletionStatus.NotFound);
        if (run.LifecycleState != ResourceLifecycleStates.Active) return new(ResourceDeletionStatus.AlreadyPending, await ExistingJobIdAsync(CleanupTargetTypes.ParseRun, parseRunId, cancellationToken));
        if (!FinalStatuses.Contains(run.Status)) return new(ResourceDeletionStatus.ActiveParseRuns);
        run.LifecycleState = ResourceLifecycleStates.DeletionPending;
        run.DeletionRequestedAtUtc = nowUtc;
        var refs = StorageRefsForRun(run).Distinct(StringComparer.Ordinal).ToArray();
        var job = NewJob(CleanupTargetTypes.ParseRun, parseRunId, refs, nowUtc);
        dbContext.CleanupJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(ResourceDeletionStatus.Accepted, job.Id);
    }

    private Task<Guid?> ExistingJobIdAsync(string type, Guid id, CancellationToken cancellationToken) => dbContext.CleanupJobs.AsNoTracking().Where(job => job.TargetType == type && job.TargetId == id).Select(job => (Guid?)job.Id).SingleOrDefaultAsync(cancellationToken);
    private static IEnumerable<string> StorageRefsForRun(ParseRunEntity run)
    {
        foreach (var value in run.Assets.Select(asset => asset.StorageRef).Concat(run.Artifacts.Select(artifact => artifact.StorageRef))) yield return value;
        yield return $"parse-runs/{run.Id:N}/provider/result.zip";
        foreach (var segment in run.Segments)
        {
            yield return segment.StorageRef;
            yield return $"parse-runs/{segment.Id:N}/provider/result.zip";
        }
        if (run.ConversionJson is not null)
        {
            StructaDoc.Application.ParseRuns.ParseRunConversion? conversion = null;
            try { conversion = StructaDoc.Application.ParseRuns.ParseRunConversion.FromJson(run.ConversionJson); } catch (JsonException) { }
            if (conversion is not null) yield return conversion.StorageRef;
        }
    }
    private static CleanupJobEntity NewJob(string type, Guid id, IReadOnlyList<string> refs, DateTime now) => new() { Id = Guid.NewGuid(), TargetType = type, TargetId = id, StorageRefsJson = JsonSerializer.Serialize(refs), Status = CleanupJobStatuses.Pending, NextAttemptAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now };

    private static IQueryable<DocumentEntity> ApplyAccess(IQueryable<DocumentEntity> query, ResourceAccessContext access, DocumentPermissions permission) => access.IsAdministrator ? query : access.HasPrincipalIdentity ? query.Where(document => document.OwnerIssuer == access.Issuer && document.OwnerSubject == access.Subject || document.AccessGrants.Any(grant => grant.PrincipalIssuer == access.Issuer && grant.PrincipalSubject == access.Subject && (grant.Permissions & (int)permission) == (int)permission)) : query.Where(_ => false);
    private static IQueryable<ParseRunEntity> ApplyRunAccess(IQueryable<ParseRunEntity> query, ResourceAccessContext access, DocumentPermissions permission) => access.IsAdministrator ? query : access.HasPrincipalIdentity ? query.Where(run => run.Document.OwnerIssuer == access.Issuer && run.Document.OwnerSubject == access.Subject || run.Document.AccessGrants.Any(grant => grant.PrincipalIssuer == access.Issuer && grant.PrincipalSubject == access.Subject && (grant.Permissions & (int)permission) == (int)permission)) : query.Where(_ => false);
}
