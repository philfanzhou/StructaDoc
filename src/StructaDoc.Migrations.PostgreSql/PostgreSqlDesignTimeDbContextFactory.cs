using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Migrations.PostgreSql;

public sealed class PostgreSqlDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<StructaDocDbContext>
{
    public StructaDocDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=structadoc;Username=structadoc;Password=unused",
            postgreSql => postgreSql.MigrationsAssembly(
                typeof(PostgreSqlDesignTimeDbContextFactory).Assembly));

        return new StructaDocDbContext(optionsBuilder.Options);
    }
}
