using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.Persistence.ParseRuns;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.Conversion;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.Workers;

namespace StructaDoc.Host.Tests;

public sealed partial class ParseRunExecutorTests(StructaDocWebApplicationFactory factory)
    : IClassFixture<StructaDocWebApplicationFactory>
{
    [Fact]
    public async Task Executor_runs_provider_result_pipeline_to_canonical_success()
    {
        var provider = new TestParseProvider(failSubmission: false);
        var result = await ExecuteAsync(provider);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Equal("local-task-1", result.ExternalTaskId);
        Assert.NotNull(result.ResultSha256);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(1, provider.StatusCount);
        Assert.Equal(1, provider.ResultCount);
        Assert.True(result.ArtifactCount >= 2);
    }

    [Fact]
    public async Task Executor_does_not_retry_an_atomic_submission_with_unknown_outcome()
    {
        var provider = new TestParseProvider(failSubmission: true);
        var result = await ExecuteAsync(provider);

        Assert.Equal(ParseRunStatuses.Failed, result.Status);
        Assert.Equal("provider-submission-outcome-unknown", result.ErrorCode);
        Assert.Null(result.ExternalTaskId);
        Assert.Equal(1, provider.SubmitCount);
        Assert.Equal(0, provider.StatusCount);
        Assert.Equal(0, provider.ResultCount);
    }

    [Fact]
    public async Task Executor_persists_and_clears_a_checkpoint_before_cloud_upload()
    {
        var provider = new TestParseProvider(failSubmission: false, useCheckpoint: true);
        var result = await ExecuteAsync(provider);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal("cloud-batch-1", result.ExternalTaskId);
        Assert.Null(result.ProtectedSubmissionContinuation);
        Assert.Equal(1, provider.PrepareCount);
        Assert.Equal(1, provider.SubmitCount);
        Assert.True(provider.SubmitObservedCheckpoint);
    }

    [Theory]
    [InlineData("application/msword", ".doc")]
    [InlineData("application/vnd.ms-excel", ".xls")]
    [InlineData("application/vnd.ms-powerpoint", ".ppt")]
    public async Task Executor_converts_a_legacy_office_source_for_mineru_local(
        string sourceMediaType,
        string sourceExtension)
    {
        var provider = new TestParseProvider(failSubmission: false);
        var converter = new TestDocumentConverter();

        var result = await ExecuteAsync(provider, sourceMediaType, converter);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal(DocumentConversionMediaTypes.Pdf, result.SubmittedMediaType);
        var conversion = ParseRunConversion.FromJson(Assert.IsType<string>(result.ConversionJson));
        Assert.Equal(sourceMediaType, conversion.SourceMediaType);
        Assert.Equal(DocumentConversionMediaTypes.Pdf, conversion.OutputMediaType);
        Assert.Equal(1, converter.ConversionCount);
        Assert.True(converter.ResultDisposed);
        Assert.Equal(DocumentConversionMediaTypes.Pdf, provider.SubmittedMediaType);
        Assert.True(result.ArtifactCount >= 3);
        Assert.True(result.HasConversionArtifact);
        Assert.True(result.HasDownloadableConversionArtifact);
        Assert.Equal(sourceMediaType, result.DocumentMediaType);
        Assert.Equal(sourceExtension, result.DocumentExtension);
        Assert.Equal(
            $"documents/{result.ParseRunId:N}/source{sourceExtension}",
            result.DocumentStorageRef);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(CreateSourceBytes(sourceMediaType)))
                .ToLowerInvariant(),
            result.DocumentSha256);
        Assert.True(result.OriginalSourcePreserved);
    }

    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx")]
    public async Task Executor_submits_a_native_ooxml_source_without_conversion(
        string sourceMediaType,
        string sourceExtension)
    {
        var provider = new TestParseProvider(
            failSubmission: false,
            supportedMediaTypes: [DocumentConversionMediaTypes.Pdf, sourceMediaType]);
        var converter = new TestDocumentConverter();

        var result = await ExecuteAsync(provider, sourceMediaType, converter);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal(sourceMediaType, result.SubmittedMediaType);
        Assert.Null(result.ConversionJson);
        Assert.Equal(0, converter.ConversionCount);
        Assert.False(converter.ResultDisposed);
        Assert.Equal(sourceMediaType, provider.SubmittedMediaType);
        Assert.False(result.HasConversionArtifact);
        Assert.False(result.HasDownloadableConversionArtifact);
        Assert.Equal(sourceMediaType, result.DocumentMediaType);
        Assert.Equal(sourceExtension, result.DocumentExtension);
        Assert.Equal(
            $"documents/{result.ParseRunId:N}/source{sourceExtension}",
            result.DocumentStorageRef);
        Assert.True(result.OriginalSourcePreserved);
    }

    [Fact]
    public async Task Executor_reuses_a_persisted_conversion_after_recovery()
    {
        const string sourceMediaType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var provider = new TestParseProvider(failSubmission: false);
        var converter = new TestDocumentConverter();

        var result = await ExecuteAsync(
            provider,
            sourceMediaType,
            converter,
            seedConversion: true);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal(0, converter.ConversionCount);
        Assert.Equal(DocumentConversionMediaTypes.Pdf, provider.SubmittedMediaType);
        Assert.True(result.HasConversionArtifact);
    }

    [Fact]
    public async Task Executor_ends_an_unresponsive_provider_attempt_at_the_execution_deadline()
    {
        var provider = new TestParseProvider(failSubmission: false, stayRunning: true);

        var result = await ExecuteAsync(
            provider,
            workerSettings: new Dictionary<string, string>
            {
                ["Worker:LeaseDuration"] = "00:00:05",
                ["Worker:HeartbeatInterval"] = "00:00:00.100",
                ["Worker:MaxExecutionDuration"] = "00:00:02",
                ["Worker:MinimumPollDelay"] = "00:00:00.200",
                ["Worker:MaximumPollDelay"] = "00:00:00.200",
            });

        // Without a deadline this attempt would poll forever, holding its slot and blocking its
        // Document from ever being deleted.
        Assert.Equal(ParseRunStatuses.RetryWait, result.Status);
        Assert.Equal("parse-run-execution-timeout", result.ErrorCode);
        Assert.Equal("local-task-1", result.ExternalTaskId);
        Assert.True(provider.StatusCount > 1);
    }

    [Fact]
    public async Task Large_pdf_orchestrator_segments_and_merges_a_real_multi_page_pdf()
    {
        var provider = new TestParseProvider(
            failSubmission: false,
            maxPages: 2,
            segmentContentLists: true);
        var storageOperations = new ConcurrentQueue<FileStorageOperation>();

        var result = await ExecuteAsync(
            provider,
            workerSettings: new Dictionary<string, string>
            {
                ["Worker:HeartbeatInterval"] = "00:00:20",
            },
            largePdfPageCount: 5,
            configureServices: services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider =>
                    new CallbackFileStorage(
                        new LocalFileStorage(
                            serviceProvider.GetRequiredService<FileStorageOptions>()),
                        storageOperations.Enqueue));
            });

        var bundle = Assert.IsType<ParseBundle>(result.MergedBundle);
        Assert.Equal(result.ParseRunId, bundle.ParseRunId);
        Assert.Equal(3, provider.SubmitCount);
        Assert.Equal(3, provider.StatusCount);
        Assert.Equal(3, provider.ResultCount);
        Assert.Collection(
            result.Segments,
            segment => Assert.Equal(new SegmentResult(0, 1, 2, "normalized"), segment),
            segment => Assert.Equal(new SegmentResult(1, 3, 4, "normalized"), segment),
            segment => Assert.Equal(new SegmentResult(2, 5, 5, "normalized"), segment));
        Assert.Equal(
            3,
            bundle.Artifacts.Count(artifact => artifact.Type == ArtifactTypes.SourceSegment));
        Assert.Single(bundle.Artifacts, artifact => artifact.Type == ArtifactTypes.Markdown);
        // Each segment's content list numbers its own pages from page_idx 0; the merge offsets
        // them by the segment's start page, so the run reads as one continuous document.
        Assert.Equal([1, 2, 3, 4, 5], bundle.Pages.Select(page => page.Number));
        const string table = "<table></table>";
        Assert.Equal(
            [
                ("segment page 1", 1),
                ("segment page 2", 2),
                (table, 2),
                ("segment page 1", 3),
                ("segment page 2", 4),
                (table, 4),
                ("segment page 1", 5),
                (table, 5),
            ],
            bundle.Blocks.Select(block => (block.Content, block.PageNumber)));
        Assert.Equal(Enumerable.Range(0, 8), bundle.Blocks.Select(block => block.Sequence));
        // Each segment's captioned table keeps its caption and footnote through the merge.
        Assert.Equal(
            [2, 5, 7],
            bundle.Blocks
                .Where(block => block.Caption == "Segment table" && block.Footnote == "Segment note")
                .Select(block => block.Sequence));
        Assert.All(
            bundle.Blocks.Where(block => block.Type == "text"),
            block =>
            {
                Assert.Null(block.Caption);
                Assert.Null(block.Footnote);
            });
        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
        Assert.Equal(
            3,
            storageOperations.Count(operation =>
                operation.Kind == FileStorageOperationKind.Write
                && operation.StorageRef.Contains("/segments/", StringComparison.Ordinal)));
        Assert.Contains(
            storageOperations,
            operation => operation.Kind == FileStorageOperationKind.Write
                && operation.StorageRef ==
                    $"parse-runs/{result.ParseRunId:N}/artifacts/document.md");
    }

    [Fact]
    public async Task Executor_commits_a_real_segmented_pdf_without_a_run_external_task()
    {
        var provider = new TestParseProvider(
            failSubmission: false,
            maxPages: 2,
            segmentContentLists: true);
        var labeledPdf = PdfTestDocument.Create(
            5,
            (0, "/r", null, null),
            (2, "/D", null, 1),
            (4, "/A", "App-", null));

        var result = await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            executeLargePdfThroughExecutor: true,
            sourceBytesOverride: labeledPdf);
        var unsplit = await ExecuteAsync(
            new TestParseProvider(failSubmission: false, segmentContentLists: true),
            sourceBytesOverride: labeledPdf);

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.ExternalTaskId);
        Assert.NotNull(result.ResultSha256);
        Assert.Equal(3, provider.SubmitCount);
        Assert.Equal(3, provider.StatusCount);
        Assert.Equal(3, provider.ResultCount);
        Assert.Collection(
            result.Segments,
            segment => Assert.Equal(new SegmentResult(0, 1, 2, "normalized"), segment),
            segment => Assert.Equal(new SegmentResult(1, 3, 4, "normalized"), segment),
            segment => Assert.Equal(new SegmentResult(2, 5, 5, "normalized"), segment));
        Assert.True(result.ArtifactCount >= 4);

        // Labels are read once from the full PDF after the merge: the segment PDFs carry no
        // page-label tree, and their page numbers restart at 1.
        Assert.Equal(
            [
                new PageResult(1, "i", PageLabelSources.PdfPageLabels),
                new PageResult(2, "ii", PageLabelSources.PdfPageLabels),
                new PageResult(3, "1", PageLabelSources.PdfPageLabels),
                new PageResult(4, "2", PageLabelSources.PdfPageLabels),
                new PageResult(5, "App-A", PageLabelSources.PdfPageLabels),
            ],
            result.Pages);
        Assert.Equal(ParseRunStatuses.Succeeded, unsplit.Status);
        Assert.Empty(unsplit.Segments);
        Assert.Equal(result.Pages, unsplit.Pages);
    }

    [Fact]
    public async Task Executor_labels_pages_from_the_submitted_pdf_page_label_tree()
    {
        var provider = new TestParseProvider(failSubmission: false, segmentContentLists: true);

        var result = await ExecuteAsync(
            provider,
            sourceBytesOverride: PdfTestDocument.Create(
                8,
                (0, "/r", null, null),
                (3, "/D", null, 1),
                (6, "/A", "App-", null)));

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Equal(
            ["i", "ii", "iii", "1", "2", "3", "App-A", "App-B"],
            result.Pages.Select(page => page.Label));
        Assert.All(result.Pages, page => Assert.Equal(PageLabelSources.PdfPageLabels, page.LabelSource));
    }

    [Theory]
    [InlineData("corrupt", "unreadable-pdf")]
    [InlineData("malformed", "malformed-page-label-tree")]
    public async Task Executor_commits_without_pdf_labels_and_warns_once_when_the_pdf_cannot_supply_them(
        string scenario,
        string expectedProblem)
    {
        var logs = new RecordingLoggerProvider();
        var malformed = scenario == "malformed";
        var provider = new TestParseProvider(failSubmission: false, segmentContentLists: malformed);
        var sourceBytes = malformed
            ? PdfTestDocument.Create(2, (0, "/Q", "Secret-", null))
            : "%PDF-1.7\nprivate document text"u8.ToArray();

        var result = await ExecuteAsync(
            provider,
            sourceBytesOverride: sourceBytes,
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.All(result.Pages, page =>
        {
            Assert.Null(page.Label);
            Assert.Null(page.LabelSource);
        });
        Assert.Equal(malformed ? 2 : 0, result.Pages.Count);
        var warning = Assert.Single(
            logs.Entries,
            entry => entry.Category == typeof(ParseRunExecutor).FullName
                && entry.Level == LogLevel.Warning);
        Assert.Contains(result.ParseRunId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(expectedProblem, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private document text", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret-", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.DocumentStorageRef, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("executor-test", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executor_keeps_the_transient_failure_when_the_source_cannot_be_read_for_labels()
    {
        var provider = new TestParseProvider(failSubmission: false);
        var failures = 0;

        var result = await ExecuteAsync(
            provider,
            configureServices: services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider =>
                    new CallbackFileStorage(
                        new LocalFileStorage(serviceProvider.GetRequiredService<FileStorageOptions>()),
                        operation =>
                        {
                            // The page-label step is the only reader of the submitted PDF on this
                            // path; the first read fails the way an unavailable store does.
                            if (operation.Kind == FileStorageOperationKind.OpenRead
                                && operation.StorageRef.StartsWith("documents/", StringComparison.Ordinal)
                                && Interlocked.Increment(ref failures) == 1)
                            {
                                throw new IOException("The storage backend is unavailable.");
                            }
                        }));
            });

        Assert.True(Volatile.Read(ref failures) >= 1, "The page-label step did not read the source.");
        Assert.Equal(1, provider.ResultCount);
        Assert.Equal(ParseRunStatuses.RetryWait, result.Status);
        Assert.Equal("parse-run-execution-error", result.ErrorCode);
        Assert.Null(result.ResultSha256);
        Assert.Empty(result.Pages);
    }

    [Fact]
    public async Task Executor_recovers_segmented_persisting_without_resubmitting_completed_segments()
    {
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
        var interruption = new CommitInterruption();

        var result = await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            executeLargePdfThroughExecutor: true,
            configureServices: services =>
            {
                services.RemoveAll<IParseBundleCommitStore>();
                services.AddScoped<IParseBundleCommitStore>(serviceProvider =>
                    new InterruptingParseBundleCommitStore(
                        new EfCoreParseBundleCommitStore(
                            serviceProvider.GetRequiredService<StructaDocDbContext>(),
                            serviceProvider.GetRequiredService<IFileStorage>(),
                            serviceProvider.GetRequiredService<TimeProvider>()),
                        interruption));
            },
            afterExecution: async (services, parseRunId) =>
            {
                await using var scope = services.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
                var interruptedRun = await dbContext.ParseRuns
                    .AsNoTracking()
                    .SingleAsync(
                        parseRun => parseRun.Id == parseRunId,
                        TestContext.Current.CancellationToken);
                Assert.Equal(ParseRunStatuses.Running, interruptedRun.Status);
                Assert.Equal(ParseRunStages.Persisting, interruptedRun.Stage);
                Assert.Null(interruptedRun.ExternalTaskId);
                Assert.Equal(3, await dbContext.ParseSegments.CountAsync(
                    segment => segment.ParseRunId == parseRunId
                        && segment.Status == "normalized",
                    TestContext.Current.CancellationToken));

                var recoveryTimeUtc = DateTime.UtcNow;
                await dbContext.ParseRuns
                    .Where(parseRun => parseRun.Id == parseRunId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            parseRun => parseRun.LeaseExpiresAtUtc,
                            recoveryTimeUtc.AddSeconds(-1)),
                        TestContext.Current.CancellationToken);
                // This host keeps the maintenance worker running at a 100 ms interval, and it
                // runs the same recovery routine. The guarded update lets exactly one caller
                // requeue an expired run, so when the worker's poll lands between the expiry
                // write above and this call, this call legitimately finds nothing to do. What
                // matters is the invariant — the run ends up queued — so accept either winner
                // instead of asserting that this call's counter did the requeue.
                var recovery = await scope.ServiceProvider
                    .GetRequiredService<IParseRunLeaseStore>()
                    .RecoverExpiredUnsubmittedRunsAsync(
                        recoveryTimeUtc,
                        maxCount: 1,
                        TestContext.Current.CancellationToken);
                Assert.Equal(0, recovery.FailedUnknownSubmissionCount);
                if (recovery.RequeuedCount == 0)
                {
                    await WaitUntilRunIsQueuedAsync(dbContext, parseRunId);
                }

                // Claim with a fresh timestamp: whichever caller requeued set the run's next
                // attempt to its own, possibly later, clock reading, and the claim only sees
                // runs whose next attempt is already due.
                var recoveredLease = Assert.IsType<ParseRunLease>(
                    await scope.ServiceProvider
                        .GetRequiredService<IParseRunLeaseStore>()
                        .TryClaimNextAsync(
                            $"recovery-{parseRunId:N}",
                            DateTime.UtcNow,
                            TimeSpan.FromMinutes(2),
                            TestContext.Current.CancellationToken));
                Assert.True(
                    recoveredLease.ParseRunId == parseRunId,
                    $"Recovery claimed run {recoveredLease.ParseRunId} instead of this test's run {parseRunId}; an earlier test left a queued run behind.");
                await scope.ServiceProvider.GetRequiredService<ParseRunExecutor>().ExecuteAsync(
                    recoveredLease,
                    alreadyRunning: false,
                    stoppingToken: TestContext.Current.CancellationToken);
            });

        Assert.Equal(1, interruption.InterruptionCount);
        Assert.Equal(ParseRunStatuses.Succeeded, result.Status);
        Assert.Null(result.ExternalTaskId);
        Assert.NotNull(result.ResultSha256);
        Assert.Equal(3, provider.SubmitCount);
        Assert.Equal(3, provider.StatusCount);
        Assert.Equal(3, provider.ResultCount);
        Assert.All(result.Segments, segment => Assert.Equal("normalized", segment.Status));
    }

    [Fact]
    public async Task Executor_propagates_the_execution_token_to_large_pdf_storage_work()
    {
        using var cancellationSource = new CancellationTokenSource();
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
        var storageOperations = new ConcurrentQueue<FileStorageOperation>();
        var segmentWriteCount = 0;

        var result = await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            executeLargePdfThroughExecutor: true,
            executionCancellationSource: cancellationSource,
            configureServices: services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider =>
                    new CallbackFileStorage(
                        new LocalFileStorage(
                            serviceProvider.GetRequiredService<FileStorageOptions>()),
                        storageOperations.Enqueue,
                        operation =>
                        {
                            if (operation.Kind == FileStorageOperationKind.Write
                                && operation.StorageRef.Contains("/segments/", StringComparison.Ordinal)
                                && Interlocked.Increment(ref segmentWriteCount) == 1)
                            {
                                cancellationSource.Cancel();
                            }
                        }));
            });

        Assert.True(provider.CapabilitiesCancellationToken.CanBeCanceled);
        var segmentWrites = storageOperations
            .Where(operation => operation.Kind == FileStorageOperationKind.Write
                && operation.StorageRef.Contains("/segments/", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(segmentWrites);
        Assert.Collection(
            result.Segments,
            segment => Assert.Equal(new SegmentResult(0, 1, 2, "creating"), segment));
        Assert.All(
            segmentWrites,
            operation => Assert.Equal(
                provider.CapabilitiesCancellationToken,
                operation.CancellationToken));
        Assert.DoesNotContain(
            storageOperations,
            operation => operation.Kind == FileStorageOperationKind.Delete
                && operation.StorageRef.Contains("/segments/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Large_pdf_persists_each_intent_before_starting_its_object_write()
    {
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
        var statusesObservedBeforeWrite = new ConcurrentQueue<string>();

        await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            configureServices: services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider =>
                    new CallbackFileStorage(
                        new LocalFileStorage(
                            serviceProvider.GetRequiredService<FileStorageOptions>()),
                        operation =>
                        {
                            if (operation.Kind != FileStorageOperationKind.Write
                                || !operation.StorageRef.Contains("/segments/", StringComparison.Ordinal))
                            {
                                return;
                            }

                            using var scope = serviceProvider.CreateScope();
                            var status = scope.ServiceProvider
                                .GetRequiredService<StructaDocDbContext>()
                                .ParseSegments
                                .AsNoTracking()
                                .Where(segment => segment.StorageRef == operation.StorageRef)
                                .Select(segment => segment.Status)
                                .Single();
                            statusesObservedBeforeWrite.Enqueue(status);
                        }));
            });

        Assert.Equal(["creating", "creating", "creating"], statusesObservedBeforeWrite);
    }

    [Fact]
    public async Task Large_pdf_does_not_write_an_object_when_intent_persistence_loses_the_lease()
    {
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
        var storageOperations = new ConcurrentQueue<FileStorageOperation>();

        var result = await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            executeLargePdfThroughExecutor: true,
            configureServices: services =>
            {
                services.RemoveAll<IParseSegmentMutationStore>();
                services.AddSingleton<IParseSegmentMutationStore>(new LeaseLosingSegmentStore());
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider =>
                    new CallbackFileStorage(
                        new LocalFileStorage(
                            serviceProvider.GetRequiredService<FileStorageOptions>()),
                        storageOperations.Enqueue));
            });

        Assert.Empty(result.Segments);
        Assert.DoesNotContain(
            storageOperations,
            operation => operation.Kind == FileStorageOperationKind.Write
                && operation.StorageRef.Contains("/segments/", StringComparison.Ordinal));
        Assert.Equal(0, provider.SubmitCount);
    }

    [Fact]
    public async Task Large_pdf_rebuilds_a_missing_object_from_a_partial_intent()
    {
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);

        var result = await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            seedExecution: (services, parseRunId, sourceBytes) =>
                SeedSegmentIntentAsync(services, parseRunId, sourceBytes, writeObject: false));

        Assert.Equal(3, result.Segments.Count);
        Assert.All(result.Segments, segment => Assert.Equal("normalized", segment.Status));
        Assert.NotNull(result.MergedBundle);
    }

    [Fact]
    public async Task Large_pdf_reuses_an_object_matching_a_partial_intent()
    {
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);

        var result = await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            seedExecution: (services, parseRunId, sourceBytes) =>
                SeedSegmentIntentAsync(services, parseRunId, sourceBytes, writeObject: true));

        Assert.Equal(3, result.Segments.Count);
        Assert.All(result.Segments, segment => Assert.Equal("normalized", segment.Status));
        Assert.NotNull(result.MergedBundle);
    }

    [Fact]
    public async Task Large_pdf_fails_permanently_when_a_partial_intent_object_conflicts()
    {
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);

        var result = await ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            executeLargePdfThroughExecutor: true,
            seedExecution: (services, parseRunId, sourceBytes) =>
                SeedSegmentIntentAsync(
                    services,
                    parseRunId,
                    sourceBytes,
                    writeObject: true,
                    conflictingObject: true));

        Assert.Equal(ParseRunStatuses.Failed, result.Status);
        Assert.Equal("parse-segment-object-conflict", result.ErrorCode);
        Assert.Collection(result.Segments, segment => Assert.Equal("creating", segment.Status));
        Assert.Equal(0, provider.SubmitCount);
    }

    [Fact]
    public async Task Large_pdf_orchestrator_stops_before_creating_the_next_segment_after_cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
        var storageOperations = new ConcurrentQueue<FileStorageOperation>();
        var segmentWriteCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            executionCancellationSource: cancellationSource,
            configureServices: services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider =>
                    new CallbackFileStorage(
                        new LocalFileStorage(
                            serviceProvider.GetRequiredService<FileStorageOptions>()),
                        storageOperations.Enqueue,
                        operation =>
                        {
                            if (operation.Kind == FileStorageOperationKind.Write
                                && operation.StorageRef.Contains("/segments/", StringComparison.Ordinal)
                                && Interlocked.Increment(ref segmentWriteCount) == 1)
                            {
                                cancellationSource.Cancel();
                            }
                        }));
            }));

        Assert.Equal(
            1,
            storageOperations.Count(operation =>
                operation.Kind == FileStorageOperationKind.Write
                && operation.StorageRef.Contains("/segments/", StringComparison.Ordinal)));
        Assert.Equal(0, provider.SubmitCount);
    }

    [Fact]
    public async Task Large_pdf_orchestrator_does_not_write_the_final_merge_after_cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
        var storageOperations = new ConcurrentQueue<FileStorageOperation>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            provider,
            largePdfPageCount: 5,
            executionCancellationSource: cancellationSource,
            configureServices: services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(serviceProvider =>
                    new CallbackFileStorage(
                        new LocalFileStorage(
                            serviceProvider.GetRequiredService<FileStorageOptions>()),
                        storageOperations.Enqueue,
                        operation =>
                        {
                            if (operation.Kind == FileStorageOperationKind.OpenRead
                                && operation.StorageRef.EndsWith(
                                    "/artifacts/markdown.md",
                                    StringComparison.Ordinal))
                            {
                                cancellationSource.Cancel();
                            }
                        }));
            }));

        Assert.Equal(3, provider.ResultCount);
        Assert.Equal(
            1,
            storageOperations.Count(operation =>
                operation.Kind == FileStorageOperationKind.OpenRead
                && operation.StorageRef.EndsWith(
                    "/artifacts/markdown.md",
                    StringComparison.Ordinal)));
        Assert.Equal(
            0,
            storageOperations.Count(operation =>
                operation.Kind == FileStorageOperationKind.Write
                && operation.StorageRef.EndsWith(
                    "/artifacts/document.md",
                    StringComparison.Ordinal)));
    }

    private async Task<ExecutionResult> ExecuteAsync(
        IParseProvider provider,
        string sourceMediaType = "application/pdf",
        IDocumentConverter? converter = null,
        bool seedConversion = false,
        IReadOnlyDictionary<string, string>? workerSettings = null,
        int? largePdfPageCount = null,
        Action<IServiceCollection>? configureServices = null,
        bool executeLargePdfThroughExecutor = false,
        CancellationTokenSource? executionCancellationSource = null,
        Func<IServiceProvider, Guid, byte[], Task>? seedExecution = null,
        Func<IServiceProvider, Guid, Task>? afterExecution = null,
        byte[]? sourceBytesOverride = null,
        string providerBaseUrl = "https://provider.example/",
        string? backend = null)
    {
        var executionCancellationToken = executionCancellationSource?.Token
            ?? TestContext.Current.CancellationToken;
        using var application = factory.WithWebHostBuilder(builder =>
        {
            foreach (var setting in workerSettings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(setting.Key, setting.Value);
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IParseProvider>();
                services.AddSingleton<IParseProvider>(provider);
                if (converter is not null)
                {
                    services.RemoveAll<IDocumentConverter>();
                    services.AddSingleton(converter);
                }

                configureServices?.Invoke(services);
            });
        });
        using var client = application.CreateClient();
        var parseRunId = Guid.NewGuid();
        var sourceExtension = GetSourceExtension(sourceMediaType);
        var sourceStorageRef = $"documents/{parseRunId:N}/source{sourceExtension}";
        var sourceBytes = sourceBytesOverride ?? (sourceMediaType == "application/pdf"
            ? largePdfPageCount.HasValue
                ? PdfTestDocument.Create(largePdfPageCount.Value)
                : "%PDF-1.7\nexecutor-test"u8.ToArray()
            : CreateSourceBytes(sourceMediaType));
        StoredFile storedSource;

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            await using var source = new MemoryStream(sourceBytes, writable: false);
            storedSource = await storage.WriteAsync(
                sourceStorageRef,
                source,
                sourceBytes.Length);
            ParseRunConversion? conversion = null;
            if (seedConversion)
            {
                var artifactId = Guid.NewGuid();
                var convertedBytes = "%PDF-1.7\nrecovered-conversion"u8.ToArray();
                await using var converted = new MemoryStream(convertedBytes, writable: false);
                var storedConversion = await storage.WriteAsync(
                    $"parse-runs/{parseRunId:N}/conversions/{artifactId:N}.pdf",
                    converted,
                    convertedBytes.Length);
                conversion = new ParseRunConversion(
                    "libreoffice",
                    "LibreOffice recovered-version",
                    sourceMediaType,
                    DocumentConversionMediaTypes.Pdf,
                    artifactId,
                    "normalized.pdf",
                    storedConversion.SizeBytes,
                    storedConversion.Sha256,
                    storedConversion.StorageRef,
                    "pdf");
            }

            var nowUtc = DateTime.UtcNow;
            var configId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                OriginalFileName = $"executor-test{sourceExtension}",
                MediaType = sourceMediaType,
                Extension = sourceExtension,
                SizeBytes = storedSource.SizeBytes,
                Sha256 = storedSource.Sha256,
                StorageRef = storedSource.StorageRef,
                CreatedAtUtc = nowUtc,
            };
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            dbContext.Documents.Add(document);
            dbContext.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = configId,
                Name = $"Executor Test {parseRunId:N}",
                ProviderType = provider.ProviderType,
                IsEnabled = true,
                CurrentVersionId = versionId,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            });
            dbContext.ProviderConfigVersions.Add(new ProviderConfigVersionEntity
            {
                Id = versionId,
                ProviderConfigId = configId,
                VersionNumber = 1,
                BaseUrl = providerBaseUrl,
                Backend = backend,
                CreatedAtUtc = nowUtc,
            });
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = parseRunId,
                DocumentId = document.Id,
                Status = ParseRunStatuses.Queued,
                ProviderType = provider.ProviderType,
                ProviderConfigId = configId,
                ProviderConfigVersion = versionId,
                OptionsJson = "{}",
                SourceMediaType = document.MediaType,
                SubmittedMediaType = conversion?.OutputMediaType ?? document.MediaType,
                ConversionJson = conversion?.ToJson(),
                MaxAttempts = 3,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
            });
            await dbContext.SaveChangesAsync();
            if (seedExecution is not null)
            {
                await seedExecution(scope.ServiceProvider, parseRunId, sourceBytes);
            }
        }

        ParseBundle? mergedBundle = null;
        await using (var scope = application.Services.CreateAsyncScope())
        {
            var nowUtc = DateTime.UtcNow;
            var leaseStore = scope.ServiceProvider.GetRequiredService<IParseRunLeaseStore>();
            var lease = Assert.IsType<ParseRunLease>(await leaseStore.TryClaimNextAsync(
                $"executor-test-{parseRunId:N}",
                nowUtc,
                TimeSpan.FromMinutes(2)));
            // The claim takes the globally earliest queued run, so a queued run left behind by
            // an earlier test would be executed here while this test's own run stayed
            // "queued" and every later assertion reported the wrong run's outcome. Fail fast
            // on exactly that contamination instead.
            Assert.True(
                lease.ParseRunId == parseRunId,
                $"The executor claimed run {lease.ParseRunId} instead of this test's run {parseRunId}; an earlier test left a queued run behind.");
            if (largePdfPageCount.HasValue && !executeLargePdfThroughExecutor)
            {
                await using var session = scope.ServiceProvider
                    .GetRequiredService<ParseRunLeaseHeartbeat>()
                    .StartSession(lease, executionCancellationToken);
                Assert.NotNull(await session.TryStartAsync(
                    ParseRunStages.Validating,
                    TestContext.Current.CancellationToken));
                var context = Assert.IsType<ParseRunExecutionContext>(
                    await session.LoadExecutionContextAsync(
                        TestContext.Current.CancellationToken));
                var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
                var source = new ProviderDocumentSource(
                    $"executor-test{sourceExtension}",
                    sourceMediaType,
                    storedSource.SizeBytes,
                    token => storage.OpenReadAsync(storedSource.StorageRef, token));
                var capabilities = await provider.GetCapabilitiesAsync(
                    context.ProviderConfiguration,
                    TestContext.Current.CancellationToken);
                var normalizer = scope.ServiceProvider
                    .GetServices<IProviderResultNormalizer>()
                    .Single(candidate => candidate.Supports(provider.ProviderType));
                mergedBundle = await scope.ServiceProvider
                    .GetRequiredService<LargePdfParseOrchestrator>()
                    .ExecuteAsync(
                        session,
                        context,
                        source,
                        capabilities,
                        provider,
                        normalizer,
                        session.ExecutionCancellationToken);
            }
            else
            {
                await scope.ServiceProvider.GetRequiredService<ParseRunExecutor>().ExecuteAsync(
                    lease,
                    alreadyRunning: false,
                    stoppingToken: executionCancellationToken);
            }
        }

        if (afterExecution is not null)
        {
            await afterExecution(application.Services, parseRunId);
        }

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StructaDocDbContext>();
            var run = await dbContext.ParseRuns
                .AsNoTracking()
                .SingleAsync(item => item.Id == parseRunId);
            var artifactCount = await dbContext.ParseArtifacts.CountAsync(
                artifact => artifact.ParseRunId == parseRunId);
            var conversionArtifact = await dbContext.ParseArtifacts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    artifact => artifact.ParseRunId == parseRunId
                        && artifact.Type == "normalized-pdf");
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var hasDownloadableConversionArtifact = false;
            if (conversionArtifact is not null)
            {
                await using var content = await storage.OpenReadAsync(
                    conversionArtifact.StorageRef,
                    TestContext.Current.CancellationToken);
                var signature = new byte[5];
                hasDownloadableConversionArtifact = await content.ReadAsync(
                    signature,
                    TestContext.Current.CancellationToken) == signature.Length
                    && signature.AsSpan().SequenceEqual("%PDF-"u8);
            }
            var document = await dbContext.Documents
                .AsNoTracking()
                .SingleAsync(item => item.Id == run.DocumentId);
            await using var original = await storage.OpenReadAsync(
                document.StorageRef,
                TestContext.Current.CancellationToken);
            var originalSha256 = Convert.ToHexString(await SHA256.HashDataAsync(
                    original,
                    TestContext.Current.CancellationToken))
                .ToLowerInvariant();
            var segments = await dbContext.ParseSegments
                .AsNoTracking()
                .Where(segment => segment.ParseRunId == parseRunId)
                .OrderBy(segment => segment.Index)
                .Select(segment => new SegmentResult(
                    segment.Index,
                    segment.StartPage,
                    segment.EndPage,
                    segment.Status))
                .ToListAsync();
            var pages = await dbContext.ParsePages
                .AsNoTracking()
                .Where(page => page.ParseRunId == parseRunId)
                .OrderBy(page => page.Number)
                .Select(page => new PageResult(page.Number, page.Label, page.LabelSource))
                .ToListAsync();
            return new ExecutionResult(
                parseRunId,
                run.Status,
                run.ErrorCode,
                run.ExternalTaskId,
                run.ProtectedSubmissionContinuation,
                run.ResultSha256,
                run.SubmittedMediaType,
                run.ConversionJson,
                artifactCount,
                conversionArtifact is not null,
                hasDownloadableConversionArtifact,
                document.MediaType,
                document.Extension,
                document.StorageRef,
                document.Sha256,
                string.Equals(document.Sha256, originalSha256, StringComparison.Ordinal),
                segments,
                mergedBundle,
                pages);
        }
    }

    private static string GetSourceExtension(string mediaType) => mediaType switch
    {
        "application/pdf" => ".pdf",
        "application/msword" => ".doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.ms-excel" => ".xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.ms-powerpoint" => ".ppt",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unsupported test media type."),
    };

    private static byte[] CreateSourceBytes(string mediaType) =>
        System.Text.Encoding.UTF8.GetBytes($"executor-source:{mediaType}");

    private static async Task WaitUntilRunIsQueuedAsync(
        StructaDocDbContext dbContext,
        Guid parseRunId)
    {
        // The maintenance worker's 100 ms interval requeues an expired run within a few
        // cycles; the generous bound only tolerates a heavily loaded test runner.
        var deadlineUtc = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var status = await dbContext.ParseRuns
                .AsNoTracking()
                .Where(parseRun => parseRun.Id == parseRunId)
                .Select(parseRun => parseRun.Status)
                .SingleAsync(TestContext.Current.CancellationToken);
            if (status == ParseRunStatuses.Queued)
            {
                return;
            }

            if (DateTime.UtcNow >= deadlineUtc)
            {
                Assert.Fail(
                    $"Run {parseRunId} was still '{status}' 10 seconds after its lease expired; neither this test's recovery call nor the maintenance worker requeued it.");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task SeedSegmentIntentAsync(
        IServiceProvider services,
        Guid parseRunId,
        byte[] sourceBytes,
        bool writeObject,
        bool conflictingObject = false)
    {
        var segmentBytes = CreateSegmentBytes(sourceBytes, parseRunId, 0, 2);
        var storageRef = $"parse-runs/{parseRunId:N}/segments/0000.pdf";
        var segment = new ParseSegmentEntity
        {
            Id = DeterministicSegmentId(parseRunId, "segment:0:0:2"),
            ParseRunId = parseRunId,
            Index = 0,
            StartPage = 1,
            EndPage = 2,
            StorageRef = storageRef,
            SizeBytes = segmentBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(segmentBytes)).ToLowerInvariant(),
            Status = "creating",
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var dbContext = services.GetRequiredService<StructaDocDbContext>();
        dbContext.ParseSegments.Add(segment);
        await dbContext.SaveChangesAsync();
        if (writeObject)
        {
            var bytes = conflictingObject ? "different-segment-content"u8.ToArray() : segmentBytes;
            await using var content = new MemoryStream(bytes, writable: false);
            await services.GetRequiredService<IFileStorage>().WriteAsync(
                storageRef,
                content,
                bytes.Length,
                TestContext.Current.CancellationToken);
        }
    }

    private static byte[] CreateSegmentBytes(
        byte[] sourceBytes,
        Guid parseRunId,
        int start,
        int count)
    {
        using var input = new MemoryStream(sourceBytes, writable: false);
        using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        for (var index = start; index < start + count; index++) output.AddPage(source.Pages[index]);
        output.Info.CreationDate = DateTime.UnixEpoch;
        output.Info.ModificationDate = DateTime.UnixEpoch;
        var documentIdBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{parseRunId:N}:segment-pdf:{start}:{count}"));
        var documentId = Convert.ToHexString(documentIdBytes.AsSpan(0, 16));
        output.Internals.FirstDocumentID = documentId;
        output.Internals.SecondDocumentID = documentId;
        using var content = new MemoryStream();
        output.Save(content, closeStream: false);
        var bytes = content.ToArray();
        var stableUuid = System.Text.Encoding.ASCII.GetBytes(
            new Guid(documentIdBytes.AsSpan(0, 16)).ToString("D"));
        NormalizeXmpUuid(bytes, "<xmpMM:DocumentID>uuid:"u8, stableUuid);
        NormalizeXmpUuid(bytes, "<xmpMM:InstanceID>uuid:"u8, stableUuid);
        return bytes;
    }

    private static void NormalizeXmpUuid(
        byte[] bytes,
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> stableUuid)
    {
        var offset = bytes.AsSpan().IndexOf(prefix);
        if (offset < 0) return;
        var valueStart = offset + prefix.Length;
        if (valueStart + stableUuid.Length > bytes.Length) return;
        stableUuid.CopyTo(bytes.AsSpan(valueStart));
    }

    private static Guid DeterministicSegmentId(Guid parseRunId, string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{parseRunId:N}:{value}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed class TestParseProvider(
        bool failSubmission,
        bool useCheckpoint = false,
        bool stayRunning = false,
        int? maxPages = null,
        IReadOnlyCollection<string>? supportedMediaTypes = null,
        bool segmentContentLists = false) : IParseProvider
    {
        private readonly ConcurrentDictionary<string, int> pageCountsByTask = new(StringComparer.Ordinal);

        public string ProviderType => useCheckpoint
            ? ProviderTypes.MinerUCloud
            : ProviderTypes.MinerULocal;

        public int PrepareCount { get; private set; }

        public int SubmitCount { get; private set; }

        public int StatusCount { get; private set; }

        public int ResultCount { get; private set; }

        public bool SubmitObservedCheckpoint { get; private set; }

        public string? SubmittedMediaType { get; private set; }

        public CancellationToken CapabilitiesCancellationToken { get; private set; }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            ProviderExecutionConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            CapabilitiesCancellationToken = cancellationToken;
            return Task.FromResult(new ProviderCapabilities(
                supportedMediaTypes ?? ["application/pdf"],
                maxFileBytes: 1024 * 1024,
                maxPages,
                supportsCancellation: false));
        }

        public Task<ProviderSubmissionCheckpoint?> PrepareSubmissionAsync(
            ProviderExecutionConfiguration configuration,
            Guid parseRunId,
            ProviderDocumentSource source,
            string optionsJson,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult<ProviderSubmissionCheckpoint?>(
                useCheckpoint
                    ? new ProviderSubmissionCheckpoint(
                        "cloud-batch-1",
                        "https://upload.example/signed?secret=value")
                    : null);
        }

        public async Task<ProviderSubmission> SubmitAsync(
            ProviderExecutionConfiguration configuration,
            Guid parseRunId,
            ProviderDocumentSource source,
            string optionsJson,
            ProviderSubmissionCheckpoint? checkpoint,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            SubmitObservedCheckpoint = checkpoint is not null;
            SubmittedMediaType = source.MediaType;
            if (failSubmission)
            {
                throw new ProviderException(
                    "provider-network-error",
                    "The Provider request failed due to a network error.",
                    ProviderFailureCategory.Transient);
            }

            if (segmentContentLists)
            {
                // Each segment gets its own task so its result can describe exactly its pages.
                var taskId = $"local-task-{SubmitCount}";
                pageCountsByTask[taskId] = await CountPdfPagesAsync(source, cancellationToken);
                return new ProviderSubmission(taskId);
            }

            return new ProviderSubmission(useCheckpoint ? "cloud-batch-1" : "local-task-1");
        }

        public Task<ProviderTaskStatus> GetStatusAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default)
        {
            StatusCount++;
            return Task.FromResult(new ProviderTaskStatus(
                stayRunning ? ProviderTaskState.Running : ProviderTaskState.Succeeded));
        }

        public Task<ProviderResultContent> OpenResultAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default)
        {
            ResultCount++;
            return Task.FromResult(new ProviderResultContent(
                CreateResultArchive(
                    segmentContentLists ? pageCountsByTask[externalTaskId] : null),
                "application/zip",
                "result.zip"));
        }

        public Task TryCancelAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static async Task<int> CountPdfPagesAsync(
            ProviderDocumentSource source,
            CancellationToken cancellationToken)
        {
            await using var content = await source.OpenReadAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            using var pdf = PdfReader.Open(buffer, PdfDocumentOpenMode.Import);
            return pdf.PageCount;
        }

        private static Stream CreateResultArchive(int? pageCount)
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var markdown = archive.CreateEntry("full.md");
                using (var writer = new StreamWriter(markdown.Open()))
                {
                    writer.Write("# Executor result");
                }

                if (pageCount.HasValue)
                {
                    // Zero-based page_idx per segment, as MinerU writes it for each submitted file.
                    var items = Enumerable.Range(0, pageCount.Value).Select(index =>
                        $$"""{"type":"text","text":"segment page {{index + 1}}","bbox":[100,100,900,200],"page_idx":{{index}}}""")
                        // One captioned table on the segment's last page, so the merge is seen to
                        // keep Block captions and footnotes.
                        .Append(
                            $$"""{"type":"table","table_body":"<table></table>","table_caption":["Segment table"],"table_footnote":["Segment note"],"page_idx":{{pageCount.Value - 1}}}""");
                    using var writer = new StreamWriter(archive.CreateEntry("content_list.json").Open());
                    writer.Write($"[{string.Join(',', items)}]");
                }
            }

            stream.Position = 0;
            return stream;
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> Entries => entries.ToList();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string category,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Information)
                {
                    entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private sealed class LeaseLosingSegmentStore : IParseSegmentMutationStore
    {
        public Task<ParseRunLease?> TryCreateAsync(
            ParseRunLease currentLease,
            IReadOnlyList<ParseSegmentCreation> segments,
            DateTime nowUtc,
            CancellationToken cancellationToken = default) => Task.FromResult<ParseRunLease?>(null);

        public Task<ParseRunLease?> TryUpdateCheckpointAsync(
            ParseRunLease currentLease,
            ParseSegmentCheckpoint checkpoint,
            DateTime nowUtc,
            CancellationToken cancellationToken = default) => Task.FromResult<ParseRunLease?>(null);
    }

    private sealed class CommitInterruption
    {
        private int interruptionCount;

        public int InterruptionCount => Volatile.Read(ref interruptionCount);

        public bool TryInterrupt() =>
            Interlocked.CompareExchange(ref interruptionCount, 1, 0) == 0;
    }

    private sealed class InterruptingParseBundleCommitStore(
        IParseBundleCommitStore inner,
        CommitInterruption interruption) : IParseBundleCommitStore
    {
        public Task<ParseBundleVerificationResult> VerifyBundleAsync(
            ParseBundle bundle,
            CancellationToken cancellationToken = default) =>
            inner.VerifyBundleAsync(bundle, cancellationToken);

        public Task<ParseBundleCommitResult> TryCommitAsync(
            ParseRunLease currentLease,
            VerifiedParseBundle verifiedBundle,
            CancellationToken cancellationToken = default) =>
            interruption.TryInterrupt()
                ? Task.FromResult(new ParseBundleCommitResult(ParseBundleCommitStatus.LeaseLost))
                : inner.TryCommitAsync(
                    currentLease,
                    verifiedBundle,
                    cancellationToken);
    }

    private sealed class TestDocumentConverter : IDocumentConverter
    {
        public int ConversionCount { get; private set; }

        public bool ResultDisposed { get; private set; }

        public bool Supports(string sourceMediaType, string outputMediaType) =>
            sourceMediaType is
                "application/msword"
                or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                or "application/vnd.ms-excel"
                or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                or "application/vnd.ms-powerpoint"
                or "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            && string.Equals(
                outputMediaType,
                DocumentConversionMediaTypes.Pdf,
                StringComparison.OrdinalIgnoreCase);

        public Task<DocumentConversionResult> ConvertAsync(
            DocumentConversionRequest request,
            CancellationToken cancellationToken = default)
        {
            ConversionCount++;
            var bytes = "%PDF-1.7\nconverted-office"u8.ToArray();
            return Task.FromResult(new DocumentConversionResult(
                "libreoffice",
                "LibreOffice test-version",
                DocumentConversionMediaTypes.Pdf,
                bytes.Length,
                new MemoryStream(bytes, writable: false),
                () =>
                {
                    ResultDisposed = true;
                    return ValueTask.CompletedTask;
                }));
        }
    }

    private sealed record ExecutionResult(
        Guid ParseRunId,
        string Status,
        string? ErrorCode,
        string? ExternalTaskId,
        string? ProtectedSubmissionContinuation,
        string? ResultSha256,
        string SubmittedMediaType,
        string? ConversionJson,
        int ArtifactCount,
        bool HasConversionArtifact,
        bool HasDownloadableConversionArtifact,
        string DocumentMediaType,
        string DocumentExtension,
        string DocumentStorageRef,
        string DocumentSha256,
        bool OriginalSourcePreserved,
        IReadOnlyList<SegmentResult> Segments,
        ParseBundle? MergedBundle,
        IReadOnlyList<PageResult> Pages);

    private sealed record PageResult(
        int Number,
        string? Label,
        string? LabelSource);

    private sealed record SegmentResult(
        int Index,
        int StartPage,
        int EndPage,
        string Status);
}
