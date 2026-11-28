namespace StructaDoc.Application.ParseRuns;

public interface IRunConversionStore
{
    Task<RunLease?> TrySaveAsync(
        RunLease currentLease,
        RunConversion conversion,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
