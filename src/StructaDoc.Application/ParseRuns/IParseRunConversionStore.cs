namespace StructaDoc.Application.ParseRuns;

public interface IParseRunConversionStore
{
    Task<ParseRunLease?> TrySaveAsync(
        ParseRunLease currentLease,
        ParseRunConversion conversion,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
