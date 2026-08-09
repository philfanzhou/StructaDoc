using Microsoft.EntityFrameworkCore;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Domain.ParseRuns;

namespace StructaDoc.Adapters.Persistence.ParseRuns;

public sealed class EfCoreParseRunConversionStore(StructaDocDbContext dbContext)
    : IParseRunConversionStore
{
    public async Task<ParseRunLease?> TrySaveAsync(
        ParseRunLease currentLease,
        ParseRunConversion conversion,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentLease);
        ArgumentNullException.ThrowIfNull(conversion);
        conversion.Validate();
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Conversion timestamps must use UTC.", nameof(nowUtc));
        }

        var conversionJson = conversion.ToJson();
        var affectedRows = await dbContext.ParseRuns
            .Where(parseRun =>
                parseRun.Id == currentLease.ParseRunId
                && parseRun.Status == ParseRunStatuses.Running
                && parseRun.Stage == ParseRunStages.Converting
                && parseRun.ExternalTaskId == null
                && parseRun.ConversionJson == null
                && parseRun.SourceMediaType == conversion.SourceMediaType
                && parseRun.ClaimedBy == currentLease.WorkerId
                && parseRun.ConcurrencyVersion == currentLease.ConcurrencyVersion
                && parseRun.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(parseRun => parseRun.ConversionJson, conversionJson)
                    .SetProperty(parseRun => parseRun.SubmittedMediaType, conversion.OutputMediaType)
                    .SetProperty(parseRun => parseRun.Stage, ParseRunStages.PreparingSource)
                    .SetProperty(
                        parseRun => parseRun.ConcurrencyVersion,
                        parseRun => parseRun.ConcurrencyVersion + 1),
                cancellationToken);

        return affectedRows == 1
            ? currentLease with
            {
                ConcurrencyVersion = currentLease.ConcurrencyVersion + 1,
            }
            : null;
    }
}
