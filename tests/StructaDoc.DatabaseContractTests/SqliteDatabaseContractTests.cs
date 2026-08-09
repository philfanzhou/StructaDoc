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
            await ParseRunLeaseContract.AssertAsync(
                DatabaseProvider.Sqlite,
                $"Data Source={Path.Combine(directoryPath, "structadoc.db")};Pooling=False");
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
