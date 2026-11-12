namespace StructaDoc.Adapters.Providers;

/// <summary>
/// The bound form of the <c>Recognition</c> settings section: whether the non-archiving image
/// text-recognition mode is on (ADR-0015). Off is the shipped state, so a deployment that never
/// turns it on registers no recognition implementation and makes no recognition request. The
/// setting is restart-bound, like <c>Layout:Backend</c>, and binds recoverably at startup.
/// </summary>
public sealed class RecognitionOptions
{
    public const string SectionName = "Recognition";

    public bool Enabled { get; init; }
}
