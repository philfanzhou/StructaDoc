using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Host.Workers;

public sealed class ParseRunExecutor(
    IParseProviderResolver providerResolver,
    IEnumerable<IProviderResultNormalizer> normalizers,
    IProviderResultIntake resultIntake,
    IFileStorage fileStorage,
    ParseRunLeaseHeartbeat heartbeat,
    ParseRunWorkerOptions options,
    ILogger<ParseRunExecutor> logger)
{
    private readonly IReadOnlyList<IProviderResultNormalizer> normalizers = normalizers.ToArray();

    public async Task ExecuteAsync(
        ParseRunLease lease,
        bool alreadyRunning,
        CancellationToken stoppingToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var session = heartbeat.StartSession(lease, stoppingToken);

        try
        {
            if (!alreadyRunning
                && await session.TryStartAsync(
                    ParseRunStages.Validating,
                    stoppingToken) is null)
            {
                return;
            }

            var context = await session.LoadExecutionContextAsync(stoppingToken)
                ?? throw Failure(
                    "parse-run-execution-context-unavailable",
                    "The Parse Run execution context is unavailable.",
                    retryable: false);
            var provider = providerResolver.Resolve(context.ProviderConfiguration.ProviderType)
                ?? throw Failure(
                    "parse-provider-not-registered",
                    "The configured parser Provider is not registered.",
                    retryable: false);
            var normalizer = ResolveNormalizer(context.ProviderConfiguration.ProviderType);
            var source = CreateSource(context);

            var externalTaskId = context.ExternalTaskId;
            var checkpoint = context.SubmissionCheckpoint;
            var stage = context.Stage;

            if (externalTaskId is null)
            {
                await ValidateAndPrepareSourceAsync(
                    session,
                    provider,
                    context,
                    source,
                    stoppingToken);
                stage = ParseRunStages.Submitting;

                try
                {
                    checkpoint = await provider.PrepareSubmissionAsync(
                        context.ProviderConfiguration,
                        context.ParseRunId,
                        source,
                        context.OptionsJson,
                        session.ExecutionCancellationToken);
                }
                catch (ProviderException exception) when (exception.Retryable)
                {
                    throw Failure(
                        "provider-submission-outcome-unknown",
                        "The Provider submission outcome is unknown and cannot be retried safely.",
                        retryable: false,
                        exception);
                }
                catch (Exception exception) when (
                    exception is not ProviderException
                    && exception is not OperationCanceledException)
                {
                    throw Failure(
                        "provider-submission-outcome-unknown",
                        "The Provider submission outcome is unknown and cannot be retried safely.",
                        retryable: false,
                        exception);
                }
                if (checkpoint is not null
                    && await session.TrySaveSubmissionCheckpointAsync(
                        checkpoint,
                        stoppingToken) is null)
                {
                    return;
                }

                ProviderSubmission submission;
                try
                {
                    submission = await provider.SubmitAsync(
                        context.ProviderConfiguration,
                        context.ParseRunId,
                        source,
                        context.OptionsJson,
                        checkpoint,
                        session.ExecutionCancellationToken);
                }
                catch (ProviderException exception) when (
                    checkpoint is null && exception.Retryable)
                {
                    throw Failure(
                        "provider-submission-outcome-unknown",
                        "The Provider submission outcome is unknown and cannot be retried safely.",
                        retryable: false,
                        exception);
                }
                catch (Exception exception) when (
                    checkpoint is null
                    && exception is not ProviderException
                    && exception is not OperationCanceledException)
                {
                    throw Failure(
                        "provider-submission-outcome-unknown",
                        "The Provider submission outcome is unknown and cannot be retried safely.",
                        retryable: false,
                        exception);
                }

                externalTaskId = ValidateSubmission(submission, checkpoint);
                var recordedLease = checkpoint is null
                    ? await session.TryRecordProviderSubmissionAsync(
                        externalTaskId,
                        stoppingToken)
                    : await session.TryCompleteSubmissionCheckpointAsync(
                        checkpoint,
                        stoppingToken);
                if (recordedLease is null)
                {
                    return;
                }

                stage = ParseRunStages.WaitingProvider;
            }
            else if (checkpoint is not null)
            {
                if (stage != ParseRunStages.Submitting)
                {
                    throw Failure(
                        "provider-submission-checkpoint-stage-invalid",
                        "The Provider submission checkpoint is inconsistent with the Parse Run stage.",
                        retryable: false);
                }

                var submission = await provider.SubmitAsync(
                    context.ProviderConfiguration,
                    context.ParseRunId,
                    source,
                    context.OptionsJson,
                    checkpoint,
                    session.ExecutionCancellationToken);
                ValidateSubmission(submission, checkpoint);
                if (await session.TryCompleteSubmissionCheckpointAsync(
                    checkpoint,
                    stoppingToken) is null)
                {
                    return;
                }

                stage = ParseRunStages.WaitingProvider;
            }
            else if (stage == ParseRunStages.Submitting)
            {
                throw Failure(
                    "provider-submission-checkpoint-missing",
                    "The Provider submission cannot be recovered without its checkpoint.",
                    retryable: false);
            }

            var archive = await resultIntake.TryLoadArchiveAsync(
                context.ParseRunId,
                session.ExecutionCancellationToken);
            if (archive is null)
            {
                await WaitForProviderAsync(
                    provider,
                    context.ProviderConfiguration,
                    externalTaskId,
                    session.ExecutionCancellationToken);

                if (stage != ParseRunStages.Downloading
                    && await session.TryUpdateStageAsync(
                        ParseRunStages.Downloading,
                        stoppingToken) is null)
                {
                    return;
                }

                stage = ParseRunStages.Downloading;
                await using var result = await provider.OpenResultAsync(
                    context.ProviderConfiguration,
                    externalTaskId,
                    session.ExecutionCancellationToken);
                archive = await resultIntake.StoreArchiveAsync(
                    context.ParseRunId,
                    result,
                    session.ExecutionCancellationToken);
            }

            if (stage is not ParseRunStages.Normalizing and not ParseRunStages.Persisting
                && await session.TryUpdateStageAsync(
                    ParseRunStages.Normalizing,
                    stoppingToken) is null)
            {
                return;
            }

            var bundle = await normalizer.NormalizeAsync(
                new ProviderResultNormalizationRequest(
                    context.ParseRunId,
                    context.ProviderConfiguration.ProviderType,
                    archive,
                    context.ProviderConfiguration.Model,
                    context.ProviderConfiguration.Backend),
                session.ExecutionCancellationToken);

            if (stage != ParseRunStages.Persisting
                && await session.TryUpdateStageAsync(
                    ParseRunStages.Persisting,
                    stoppingToken) is null)
            {
                return;
            }

            var commit = await session.TryCommitBundleAsync(bundle, stoppingToken);
            if (commit is null
                || commit.Status is ParseBundleCommitStatus.Committed
                    or ParseBundleCommitStatus.AlreadyCommitted
                    or ParseBundleCommitStatus.LeaseLost)
            {
                return;
            }

            throw commit.Status switch
            {
                ParseBundleCommitStatus.StorageMismatch => Failure(
                    commit.ErrorCode ?? "parse-result-storage-mismatch",
                    commit.ErrorMessage ?? "The normalized result storage could not be verified.",
                    retryable: true),
                _ => Failure(
                    commit.ErrorCode ?? "parse-result-commit-failed",
                    commit.ErrorMessage ?? "The normalized result could not be committed.",
                    retryable: false),
            };
        }
        catch (OperationCanceledException) when (
            stoppingToken.IsCancellationRequested
            || session.ExecutionCancellationToken.IsCancellationRequested)
        {
        }
        catch (ExecutionFailureException exception)
        {
            await RecordFailureAsync(session, exception.ErrorCode, exception.Message, exception.Retryable, stoppingToken);
        }
        catch (ProviderException exception)
        {
            await RecordFailureAsync(session, exception.ErrorCode, exception.Message, exception.Retryable, stoppingToken);
        }
        catch (ProviderResultIntakeException exception)
        {
            await RecordFailureAsync(session, exception.ErrorCode, exception.Message, exception.Retryable, stoppingToken);
        }
        catch (ProviderResultNormalizationException exception)
        {
            await RecordFailureAsync(session, exception.ErrorCode, exception.Message, exception.Retryable, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Parse Run {ParseRunId} failed with unexpected exception type {ExceptionType}.",
                lease.ParseRunId,
                exception.GetType().FullName);
            await RecordFailureAsync(
                session,
                "parse-run-execution-error",
                "The Parse Run failed due to an unexpected execution error.",
                retryable: true,
                stoppingToken);
        }
    }

    private async Task ValidateAndPrepareSourceAsync(
        ParseRunLeaseSession session,
        IParseProvider provider,
        ParseRunExecutionContext context,
        ProviderDocumentSource source,
        CancellationToken cancellationToken)
    {
        var capabilities = await provider.GetCapabilitiesAsync(
            context.ProviderConfiguration,
            session.ExecutionCancellationToken);
        if (!capabilities.SupportsMediaType(context.SubmittedMediaType))
        {
            throw Failure(
                "provider-source-media-type-unsupported",
                "The Provider does not support the submitted document media type.",
                retryable: false);
        }

        if (capabilities.MaxFileBytes.HasValue
            && context.SourceSizeBytes > capabilities.MaxFileBytes.Value)
        {
            throw Failure(
                "provider-source-file-too-large",
                "The source document exceeds the Provider file size limit.",
                retryable: false);
        }

        if (await session.TryUpdateStageAsync(
            ParseRunStages.PreparingSource,
            cancellationToken) is null)
        {
            throw new OperationCanceledException(session.ExecutionCancellationToken);
        }

        if (!string.Equals(
            context.SourceMediaType,
            context.SubmittedMediaType,
            StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                "source-conversion-not-available",
                "The prepared source requires a converter that is not available.",
                retryable: false);
        }

        if (await session.TryUpdateStageAsync(
            ParseRunStages.Submitting,
            cancellationToken) is null)
        {
            throw new OperationCanceledException(session.ExecutionCancellationToken);
        }
    }

    private async Task WaitForProviderAsync(
        IParseProvider provider,
        ProviderExecutionConfiguration configuration,
        string externalTaskId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = await provider.GetStatusAsync(
                configuration,
                externalTaskId,
                cancellationToken);
            switch (status.State)
            {
                case ProviderTaskState.Succeeded:
                    return;

                case ProviderTaskState.Failed:
                    throw Failure(
                        status.ErrorCode ?? "provider-task-failed",
                        status.ErrorMessage ?? "The Provider task failed.",
                        status.Retryable);

                case ProviderTaskState.Queued:
                case ProviderTaskState.Running:
                case ProviderTaskState.Unknown:
                    await Task.Delay(
                        NormalizePollDelay(status.SuggestedPollDelay),
                        cancellationToken);
                    break;

                default:
                    throw Failure(
                        "provider-task-state-invalid",
                        "The Provider returned an invalid task state.",
                        retryable: false);
            }
        }
    }

    private IProviderResultNormalizer ResolveNormalizer(string providerType)
    {
        var matches = normalizers.Where(normalizer => normalizer.Supports(providerType)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw Failure(
                "provider-result-normalizer-not-registered",
                "No result normalizer is registered for the configured Provider.",
                retryable: false),
            _ => throw Failure(
                "provider-result-normalizer-ambiguous",
                "More than one result normalizer is registered for the configured Provider.",
                retryable: false),
        };
    }

    private ProviderDocumentSource CreateSource(ParseRunExecutionContext context) => new(
        context.OriginalFileName,
        context.SubmittedMediaType,
        context.SourceSizeBytes,
        cancellationToken => fileStorage.OpenReadAsync(
            context.SourceStorageRef,
            cancellationToken));

    private static string ValidateSubmission(
        ProviderSubmission submission,
        ProviderSubmissionCheckpoint? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (checkpoint is not null
            && !string.Equals(
                submission.ExternalTaskId,
                checkpoint.ExternalTaskId,
                StringComparison.Ordinal))
        {
            throw Failure(
                "provider-submission-id-mismatch",
                "The Provider submission ID does not match its durable checkpoint.",
                retryable: false);
        }

        return submission.ExternalTaskId;
    }

    private TimeSpan NormalizePollDelay(TimeSpan? suggestedDelay)
    {
        var delay = suggestedDelay ?? options.MinimumPollDelay;
        if (delay < options.MinimumPollDelay)
        {
            return options.MinimumPollDelay;
        }

        return delay > options.MaximumPollDelay
            ? options.MaximumPollDelay
            : delay;
    }

    private async Task RecordFailureAsync(
        ParseRunLeaseSession session,
        string errorCode,
        string safeMessage,
        bool retryable,
        CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested || session.IsLeaseLost)
        {
            return;
        }

        var transition = await session.TryRecordFailureAsync(
            errorCode,
            safeMessage,
            retryable,
            options.RetryDelay,
            stoppingToken);
        if (transition is not null)
        {
            logger.LogWarning(
                "Parse Run {ParseRunId} entered {Status} with error {ErrorCode}.",
                transition.ParseRunId,
                transition.Status,
                errorCode);
        }
    }

    private static ExecutionFailureException Failure(
        string errorCode,
        string safeMessage,
        bool retryable,
        Exception? innerException = null) =>
        new(errorCode, safeMessage, retryable, innerException);

    private sealed class ExecutionFailureException(
        string errorCode,
        string safeMessage,
        bool retryable,
        Exception? innerException = null) : Exception(safeMessage, innerException)
    {
        public string ErrorCode { get; } = errorCode;

        public bool Retryable { get; } = retryable;
    }
}
