using StructaDoc.Adapters.Persistence;
using Testcontainers.MariaDb;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace StructaDoc.DatabaseContractTests;

public sealed class ServerDatabaseContractTests
{
    [DatabaseContractFact]
    public async Task PostgreSql_satisfies_parse_run_lease_contract()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("structadoc")
            .WithUsername("structadoc")
            .WithPassword("structadoc-test")
            .Build();
        await container.StartAsync();

        await ParseRunLeaseContract.AssertAsync(
            DatabaseProvider.PostgreSql,
            container.GetConnectionString());
    }

    [DatabaseContractFact]
    public async Task MySql_satisfies_parse_run_lease_contract()
    {
        await using var container = new MySqlBuilder("mysql:8.4")
            .WithDatabase("structadoc")
            .WithPassword("structadoc-test")
            .Build();
        await container.StartAsync();

        await InnoDbMigrationPreflightContract.AssertAsync(
            DatabaseProvider.MySql,
            container.GetConnectionString(),
            serverVersion: "8.4.0");
        await ParseRunLeaseContract.AssertAsync(
            DatabaseProvider.MySql,
            container.GetConnectionString(),
            serverVersion: "8.4.0");
    }

    [DatabaseContractFact]
    public async Task MariaDb_satisfies_parse_run_lease_contract()
    {
        await using var container = new MariaDbBuilder("mariadb:11.4")
            .WithDatabase("structadoc")
            .WithPassword("structadoc-test")
            .Build();
        await container.StartAsync();

        await InnoDbMigrationPreflightContract.AssertAsync(
            DatabaseProvider.MariaDb,
            container.GetConnectionString(),
            serverVersion: "11.4.0");
        await ParseRunLeaseContract.AssertAsync(
            DatabaseProvider.MariaDb,
            container.GetConnectionString(),
            serverVersion: "11.4.0");
    }
}
