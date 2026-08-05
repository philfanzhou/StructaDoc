namespace StructaDoc.Domain.ParseRuns;

public static class ParseRunStages
{
    public const string Validating = "validating";
    public const string PreparingSource = "preparing-source";
    public const string Converting = "converting";
    public const string Submitting = "submitting";
    public const string WaitingProvider = "waiting-provider";
    public const string Downloading = "downloading";
    public const string Normalizing = "normalizing";
    public const string Persisting = "persisting";
    public const string CleaningUp = "cleaning-up";

    public static bool IsKnown(string stage)
    {
        return stage is Validating
            or PreparingSource
            or Converting
            or Submitting
            or WaitingProvider
            or Downloading
            or Normalizing
            or Persisting
            or CleaningUp;
    }
}
