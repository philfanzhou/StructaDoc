using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

public sealed class ParseRunExecutorTests(StructaDocWebApplicationFactory factory)
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
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
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
    public async Task Executor_propagates_the_execution_token_to_large_pdf_storage_work()
    {
        using var cancellationSource = new CancellationTokenSource();
        var provider = new TestParseProvider(failSubmission: false, maxPages: 2);
        var storageOperations = new ConcurrentQueue<FileStorageOperation>();
        var segmentWriteCount = 0;

        await ExecuteAsync(
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
        Assert.All(
            segmentWrites,
            operation => Assert.Equal(
                provider.CapabilitiesCancellationToken,
                operation.CancellationToken));
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
        TestParseProvider provider,
        string sourceMediaType = "application/pdf",
        IDocumentConverter? converter = null,
        bool seedConversion = false,
        IReadOnlyDictionary<string, string>? workerSettings = null,
        int? largePdfPageCount = null,
        Action<IServiceCollection>? configureServices = null,
        bool executeLargePdfThroughExecutor = false,
        CancellationTokenSource? executionCancellationSource = null)
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
        var sourceBytes = sourceMediaType == "application/pdf"
            ? largePdfPageCount.HasValue
                ? PdfTestDocument.Create(largePdfPageCount.Value)
                : "%PDF-1.7\nexecutor-test"u8.ToArray()
            : CreateSourceBytes(sourceMediaType);
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
                BaseUrl = "https://provider.example/",
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
                mergedBundle);
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

    private sealed class TestParseProvider(
        bool failSubmission,
        bool useCheckpoint = false,
        bool stayRunning = false,
        int? maxPages = null,
        IReadOnlyCollection<string>? supportedMediaTypes = null) : IParseProvider
    {
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

        public Task<ProviderSubmission> SubmitAsync(
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

            return Task.FromResult(new ProviderSubmission(
                useCheckpoint ? "cloud-batch-1" : "local-task-1"));
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
                CreateResultArchive(),
                "application/zip",
                "result.zip"));
        }

        public Task TryCancelAsync(
            ProviderExecutionConfiguration configuration,
            string externalTaskId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static Stream CreateResultArchive()
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var markdown = archive.CreateEntry("full.md");
                using var writer = new StreamWriter(markdown.Open());
                writer.Write("# Executor result");
            }

            stream.Position = 0;
            return stream;
        }
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
        ParseBundle? MergedBundle);

    private sealed record SegmentResult(
        int Index,
        int StartPage,
        int EndPage,
        string Status);
}
