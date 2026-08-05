using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.Providers;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Infrastructure.Persistence.ParseRuns;

public sealed class EfCoreParseRunExecutionContextStore(
    StructaDocDbContext dbContext,
    IProviderSecretProtector secretProtector,
    IProviderSubmissionProtector submissionProtector) : IParseRunExecutionContextStore
{
    public async Task<ParseRunExecutionContext?> LoadAsync(
        ParseRunLease currentLease,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentLease);

        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Execution timestamps must use UTC.", nameof(nowUtc));
        }

        var snapshot = await (
            from parseRun in dbContext.ParseRuns.AsNoTracking()
            join document in dbContext.Documents.AsNoTracking()
                on parseRun.DocumentId equals document.Id
            join version in dbContext.ProviderConfigVersions.AsNoTracking()
                on new { ConfigId = parseRun.ProviderConfigId, VersionId = parseRun.ProviderConfigVersion }
                equals new { ConfigId = version.ProviderConfigId, VersionId = version.Id }
            where parseRun.Id == currentLease.ParseRunId
                && (parseRun.Status == ParseRunStatuses.Claimed
                    || parseRun.Status == ParseRunStatuses.Running)
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc
            select new
            {
                parseRun.Id,
                parseRun.DocumentId,
                document.OriginalFileName,
                parseRun.SourceMediaType,
                parseRun.SubmittedMediaType,
                document.SizeBytes,
                document.Sha256,
                document.StorageRef,
                parseRun.OptionsJson,
                parseRun.Stage,
                parseRun.ExternalTaskId,
                parseRun.ProtectedSubmissionContinuation,
                parseRun.AttemptCount,
                parseRun.ProviderConfigId,
                parseRun.ProviderConfigVersion,
                parseRun.ProviderType,
                version.BaseUrl,
                version.Model,
                version.Backend,
                version.ProtectedCredential,
            }).SingleOrDefaultAsync(cancellationToken);

        if (snapshot is null)
        {
            return null;
        }


        if (snapshot.Stage is null)
        {
            return null;
        }

        var credential = snapshot.ProtectedCredential is null
            ? null
            : new ProviderCredential(secretProtector.Unprotect(snapshot.ProtectedCredential));
        var submissionCheckpoint = snapshot.ProtectedSubmissionContinuation is null
            ? null
            : new ProviderSubmissionCheckpoint(
                snapshot.ExternalTaskId
                    ?? throw new InvalidOperationException(
                        "A protected submission continuation requires an external task ID."),
                submissionProtector.Unprotect(snapshot.ProtectedSubmissionContinuation));

        return new ParseRunExecutionContext(
            snapshot.Id,
            snapshot.DocumentId,
            snapshot.OriginalFileName,
            snapshot.SourceMediaType,
            snapshot.SubmittedMediaType,
            snapshot.SizeBytes,
            snapshot.Sha256,
            snapshot.StorageRef,
            snapshot.OptionsJson,
            snapshot.Stage,
            snapshot.ExternalTaskId,
            submissionCheckpoint,
            snapshot.AttemptCount,
            new ProviderExecutionConfiguration(
                snapshot.ProviderConfigId,
                snapshot.ProviderConfigVersion,
                snapshot.ProviderType,
                new Uri(snapshot.BaseUrl, UriKind.Absolute),
                snapshot.Model,
                snapshot.Backend,
                credential));
    }
}
