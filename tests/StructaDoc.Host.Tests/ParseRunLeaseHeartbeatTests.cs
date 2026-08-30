using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Settings;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.Tests;

public sealed class ParseRunLeaseHeartbeatTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Heartbeat_and_stage_updates_share_the_latest_lease_token()
    {
        using var client = factory.CreateClient();
        var initialLease = await AddRunningParseRunAsync();
        var heartbeat = factory.Services.GetRequiredService<ParseRunLeaseHeartbeat>();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);
        var submittingLease = await session.TryUpdateStageAsync(
            ParseRunStages.Submitting,
            TestContext.Current.CancellationToken);
        Assert.NotNull(submittingLease);

        var deadlineUtc = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadlineUtc
            && session.CurrentLease.ConcurrencyVersion == submittingLease.ConcurrencyVersion)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.False(session.IsLeaseLost);
        Assert.True(
            session.CurrentLease.ConcurrencyVersion > submittingLease.ConcurrencyVersion,
            "The heartbeat did not renew the lease within three seconds.");
        Assert.True(session.CurrentLease.LeaseExpiresAtUtc > submittingLease.LeaseExpiresAtUtc);
        var preparingLease = await session.TryUpdateStageAsync(
            ParseRunStages.PreparingSource,
            TestContext.Current.CancellationToken);
        Assert.NotNull(preparingLease);
        await session.DisposeAsync();
        var finalLease = session.CurrentLease;

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var persistedRun = await dbContext.ParseRuns
            .AsNoTracking()
            .SingleAsync(
                parseRun => parseRun.Id == initialLease.ParseRunId,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ParseRunStages.PreparingSource, persistedRun.Stage);
        Assert.Equal(finalLease.ConcurrencyVersion, persistedRun.ConcurrencyVersion);
    }

    [Fact]
    public async Task Segment_mutations_advance_the_session_lease_used_by_heartbeat()
    {
        using var client = factory.CreateClient();
        var initialLease = await AddRunningParseRunAsync();
        var heartbeat = factory.Services.GetRequiredService<ParseRunLeaseHeartbeat>();
        var segmentId = Guid.NewGuid();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);
        Assert.NotNull(await session.TryUpdateStageAsync(
            ParseRunStages.Segmenting,
            TestContext.Current.CancellationToken));

        var creationLease = Assert.IsType<ParseRunLease>(
            await session.TryCreateSegmentsAsync(
                [new ParseSegmentCreation(
                    segmentId,
                    0,
                    1,
                    2,
                    $"parse-runs/{initialLease.ParseRunId:N}/segments/0000.pdf",
                    128,
                    new string('d', 64),
                    "created")],
                TestContext.Current.CancellationToken));
        var checkpointLease = Assert.IsType<ParseRunLease>(
            await session.TryUpdateSegmentCheckpointAsync(
                new ParseSegmentCheckpoint(
                    segmentId,
                    "submitted",
                    "segment-task-1",
                    null),
                TestContext.Current.CancellationToken));

        Assert.True(checkpointLease.ConcurrencyVersion > creationLease.ConcurrencyVersion);
        Assert.True(
            session.CurrentLease.ConcurrencyVersion >= checkpointLease.ConcurrencyVersion);

        var deadlineUtc = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadlineUtc
            && session.CurrentLease.ConcurrencyVersion == checkpointLease.ConcurrencyVersion)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.False(session.IsLeaseLost);
        Assert.True(session.CurrentLease.ConcurrencyVersion > checkpointLease.ConcurrencyVersion);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var persistedSegment = await dbContext.ParseSegments
            .AsNoTracking()
            .SingleAsync(
                segment => segment.Id == segmentId,
                TestContext.Current.CancellationToken);
        Assert.Equal("submitted", persistedSegment.Status);
        Assert.Equal("segment-task-1", persistedSegment.ExternalTaskId);
    }

    [Fact]
    public async Task Heartbeat_cancels_execution_when_the_lease_is_lost()
    {
        using var client = factory.CreateClient();
        var initialLease = await AddRunningParseRunAsync();
        var heartbeat = factory.Services.GetRequiredService<ParseRunLeaseHeartbeat>();

        await using var session = heartbeat.StartSession(
            initialLease,
            TestContext.Current.CancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            await dbContext.ParseRuns
                .Where(parseRun => parseRun.Id == initialLease.ParseRunId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(parseRun => parseRun.Status, ParseRunStatuses.CancelRequested)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var leaseLost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session.ExecutionCancellationToken.Register(
            () => leaseLost.TrySetResult());
        await Task.WhenAny(leaseLost.Task, Task.Delay(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));

        Assert.True(
            session.IsLeaseLost,
            "The heartbeat did not observe the rejected lease renewal within ten seconds.");
        Assert.True(session.ExecutionCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Worker_options_require_heartbeat_to_be_shorter_than_the_lease()
    {
        var options = new ParseRunWorkerOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(1),
            HeartbeatInterval = TimeSpan.FromSeconds(1),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // Configuring a Provider is the whole of what a deployment does before its documents are parsed.
    // There was once a second switch after that decision, defaulting to off, and what it produced was
    // an upload that queued forever while nothing failed and nothing was logged. This is what would
    // notice one coming back: shipped options that run Workers, and a settings catalog with nothing
    // in it that stands between an administrator's decision and the Worker acting on it.
    [Fact]
    public void Nothing_switchable_stands_between_a_configured_provider_and_execution()
    {
        Assert.True(new ParseRunWorkerOptions().Enabled);

        Assert.DoesNotContain(
            SettingCatalog.All,
            definition => definition.Key.Equals(
                "Worker:ExecutionEnabled",
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ParseRunLease> AddRunningParseRunAsync()
    {
        var nowUtc = DateTime.UtcNow;
        var parseRunId = Guid.NewGuid();
        var workerId = $"heartbeat-worker-{parseRunId:N}";
        var leaseExpiresAtUtc = nowUtc.AddSeconds(30);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "heartbeat-test.pdf",
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
            Status = ParseRunStatuses.Running,
            Stage = ParseRunStages.Validating,
            ProviderType = "heartbeat-test-provider",
            ProviderConfigId = Guid.NewGuid(),
            ProviderConfigVersion = Guid.NewGuid(),
            OptionsJson = "{}",
            SourceMediaType = "application/pdf",
            SubmittedMediaType = "application/pdf",
            AttemptCount = 1,
            MaxAttempts = 3,
            NextAttemptAtUtc = nowUtc,
            ClaimedBy = workerId,
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
            CreatedAtUtc = nowUtc,
            StartedAtUtc = nowUtc,
            ConcurrencyVersion = 1,
        });
        await dbContext.SaveChangesAsync();

        return new ParseRunLease(parseRunId, workerId, 1, leaseExpiresAtUtc);
    }
}
