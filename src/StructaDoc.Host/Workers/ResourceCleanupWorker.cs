using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.Resources;
using StructaDoc.Adapters.Persistence;

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
            db.ChangeTracker.Clear();
            var retry = await db.CleanupJobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
            retry.Status = CleanupJobStatuses.RetryWait;
            retry.ErrorMessage = exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message;
            retry.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(retry.AttemptCount, 8))));
            retry.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(exception, "Cleanup job {CleanupJobId} will be retried.", job.Id);
        }
        return true;
    }
}
