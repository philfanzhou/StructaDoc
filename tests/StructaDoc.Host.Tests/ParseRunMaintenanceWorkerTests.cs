using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.Workers;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;

namespace StructaDoc.Host.Tests;

public sealed class ParseRunMaintenanceWorkerTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Due_retry_is_returned_to_queue()
    {
        using var client = factory.CreateClient();
        var workerOptions = factory.Services.GetRequiredService<ParseRunWorkerOptions>();
        Assert.Equal(TimeSpan.FromMilliseconds(100), workerOptions.MaintenanceInterval);

        var parseRunId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "worker-test.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 128,
                Sha256 = new string('a', 64),
                StorageRef = $"documents/{parseRunId:N}.pdf",
                CreatedAtUtc = nowUtc,
            };
            dbContext.Documents.Add(document);
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = parseRunId,
                DocumentId = document.Id,
                Status = ParseRunStatuses.RetryWait,
                ProviderType = "test-provider",
                ProviderConfigId = Guid.NewGuid(),
                ProviderConfigVersion = Guid.NewGuid(),
                OptionsJson = "{}",
                SourceMediaType = "application/pdf",
                SubmittedMediaType = "application/pdf",
                AttemptCount = 1,
                MaxAttempts = 3,
                NextAttemptAtUtc = nowUtc.AddSeconds(-1),
                CreatedAtUtc = nowUtc,
                ConcurrencyVersion = 1,
            });
            await dbContext.SaveChangesAsync();
        }

        var deadlineUtc = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadlineUtc)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var status = await dbContext.ParseRuns
                .AsNoTracking()
                .Where(parseRun => parseRun.Id == parseRunId)
                .Select(parseRun => parseRun.Status)
                .SingleAsync();

            if (status == ParseRunStatuses.Queued)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The maintenance worker did not queue the due retry within five seconds.");
    }
}
