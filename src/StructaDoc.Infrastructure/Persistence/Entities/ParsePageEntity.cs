namespace StructaDoc.Infrastructure.Persistence.Entities;

public sealed class ParsePageEntity
{
    public Guid ParseRunId { get; set; }

    public ParseRunEntity ParseRun { get; set; } = null!;

    public int Number { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public string? Unit { get; set; }

    public string? SourceLocatorJson { get; set; }
}
