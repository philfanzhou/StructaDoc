using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.CleanupJobs;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.Resources;

namespace StructaDoc.Host.Workers;

public sealed class ResourceCleanupWorker(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<ResourceCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (!await ProcessOneAsync(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Resource cleanup loop failed."); await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken); }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var now = clock.GetUtcNow().UtcDateTime;
        var job = await db.CleanupJobs.Where(item => (item.Status == CleanupJobStatuses.Pending || item.Status == CleanupJobStatuses.RetryWait || item.Status == CleanupJobStatuses.Running) && item.NextAttemptAtUtc <= now).OrderBy(item => item.NextAttemptAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (job is null) return false;
        job.Status = CleanupJobStatuses.Running;
        job.AttemptCount++;
        job.NextAttemptAtUtc = now.AddMinutes(5);
        job.UpdatedAtUtc = now;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return true; }

        // The concurrency version this claim just wrote identifies the attempt. Every later
        // database write of the attempt is fenced by it: completion updates the row through the
        // tracked concurrency token, and a failure schedules its retry only while the row is still
        // running under this version. A slower attempt that was reclaimed after the five-minute
        // deadline therefore repeats idempotent object deletion but cannot touch the state, retry
        // deadline, attempt count, or error of the newer claim or of a finished job.
        var claimConcurrencyVersion = job.ConcurrencyVersion;

        try
        {
            foreach (var storageRef in JsonSerializer.Deserialize<string[]>(job.StorageRefsJson) ?? []) await storage.DeleteIfExistsAsync(storageRef, cancellationToken);
            if (job.TargetType == CleanupTargetTypes.ParseRun)
            {
                var run = await db.ParseRuns.SingleOrDefaultAsync(item => item.Id == job.TargetId, cancellationToken);
                if (run is not null) db.ParseRuns.Remove(run);
            }
            else if (job.TargetType == CleanupTargetTypes.Document)
            {
                var runs = await db.ParseRuns.Where(item => item.DocumentId == job.TargetId).ToListAsync(cancellationToken);
                db.ParseRuns.RemoveRange(runs);
                var document = await db.Documents.SingleOrDefaultAsync(item => item.Id == job.TargetId, cancellationToken);
                if (document is not null) db.Documents.Remove(document);
            }
            else throw new InvalidOperationException($"Unknown cleanup target type '{job.TargetType}'.");
            job.Status = CleanupJobStatuses.Completed;
            job.ErrorMessage = null;
            job.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Completed cleanup job {CleanupJobId} for {TargetType} {TargetId}.", job.Id, job.TargetType, job.TargetId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A DbUpdateConcurrencyException from the completion save above lands here too. When
            // the job row itself moved on, the conditional retry below matches nothing and the
            // stale attempt performs no state mutation; when only the target rows conflicted, the
            // attempt still owns the job and schedules a normal retry. Cancellation keeps escaping
            // this filter and leaves the job running for the existing stale-claim recovery.
            db.ChangeTracker.Clear();
            var failedAtUtc = clock.GetUtcNow().UtcDateTime;
            var scheduledRetry = await new EfCoreCleanupJobStateStore(db).TryScheduleRetryAsync(
                job.Id,
                claimConcurrencyVersion,
                exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message,
                failedAtUtc,
                failedAtUtc.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(job.AttemptCount, 8)))),
                cancellationToken);
            if (scheduledRetry)
            {
                logger.LogWarning(exception, "Cleanup job {CleanupJobId} will be retried.", job.Id);
            }
            else
            {
                logger.LogInformation(
                    exception,
                    "Cleanup job {CleanupJobId} was reclaimed or finished while this attempt ran; its current state is untouched.",
                    job.Id);
            }
        }
        return true;
    }
}
