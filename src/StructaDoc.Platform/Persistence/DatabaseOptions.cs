namespace StructaDoc.Platform.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; init; } = DatabaseProvider.Sqlite;

    public string ConnectionString { get; init; } = "Data Source=./data/structadoc.db";

    public string? ServerVersion { get; init; }

    public bool ApplyMigrationsOnStartup { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("Database:ConnectionString must be configured.");
        }

        if (Provider is DatabaseProvider.MySql or DatabaseProvider.MariaDb
            && !Version.TryParse(ServerVersion, out _))
        {
            throw new InvalidOperationException(
                $"Database:ServerVersion must be a valid version for {Provider}.");
        }
    }
}
