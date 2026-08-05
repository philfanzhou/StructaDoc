using Microsoft.EntityFrameworkCore;
using StructaDoc.Infrastructure.Persistence;

namespace StructaDoc.Persistence.Tests;

public sealed class DatabaseProviderConfigurationTests
{
    public static TheoryData<DatabaseProvider, string, string?, string> Providers => new()
    {
        {
            DatabaseProvider.Sqlite,
            "Data Source=structadoc.db",
            null,
            "Microsoft.EntityFrameworkCore.Sqlite"
        },
        {
            DatabaseProvider.PostgreSql,
            "Host=localhost;Database=structadoc;Username=user;Password=unused",
            null,
            "Npgsql.EntityFrameworkCore.PostgreSQL"
        },
        {
            DatabaseProvider.MySql,
            "Server=localhost;Database=structadoc;User=user;Password=unused",
            "8.4.0",
            "Microting.EntityFrameworkCore.MySql"
        },
        {
            DatabaseProvider.MariaDb,
            "Server=localhost;Database=structadoc;User=user;Password=unused",
            "11.4.0",
            "Microting.EntityFrameworkCore.MySql"
        },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void Configures_expected_ef_core_provider(
        DatabaseProvider provider,
        string connectionString,
        string? serverVersion,
        string expectedProviderName)
    {
        var databaseOptions = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
            ServerVersion = serverVersion,
        };
        var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();

        PersistenceServiceCollectionExtensions.ConfigureDatabase(optionsBuilder, databaseOptions);

        using var dbContext = new StructaDocDbContext(optionsBuilder.Options);
        Assert.Equal(expectedProviderName, dbContext.Database.ProviderName);
    }

    [Theory]
    [InlineData(DatabaseProvider.MySql)]
    [InlineData(DatabaseProvider.MariaDb)]
    public void MySql_compatible_providers_require_an_explicit_server_version(
        DatabaseProvider provider)
    {
        var databaseOptions = new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = "Server=localhost;Database=structadoc",
        };

        var exception = Assert.Throws<InvalidOperationException>(databaseOptions.Validate);

        Assert.Contains("Database:ServerVersion", exception.Message, StringComparison.Ordinal);
    }
}
