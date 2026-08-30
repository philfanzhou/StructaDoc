using StructaDoc.Adapters.Persistence;

namespace StructaDoc.DatabaseContractTests;

/// <summary>
/// SQLite runs the same lease, state, cancellation, and result contract as the server databases.
/// It needs no container, so it also keeps the contract runnable where Docker is unavailable.
/// </summary>
public sealed class SqliteDatabaseContractTests
{
    [Fact]
    public async Task Sqlite_satisfies_parse_run_lease_contract()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "structadoc-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);

        try
        {
            var connectionString = $"Data Source={Path.Combine(directoryPath, "structadoc.db")};Pooling=False";
            await DocumentIdentityMigrationContract.AssertAsync(
                DatabaseProvider.Sqlite,
                connectionString);
            await ParseRunLeaseContract.AssertAsync(
                DatabaseProvider.Sqlite,
                connectionString);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task Sqlite_rejects_invalid_identity_source_before_rebuild()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "structadoc-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);

        try
        {
            await DocumentIdentityMigrationContract.AssertSqlitePreflightAsync(
                $"Data Source={Path.Combine(directoryPath, "invalid.db")};Pooling=False");
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
