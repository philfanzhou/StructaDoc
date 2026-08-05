using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using StructaDoc.Infrastructure.Persistence;

namespace StructaDoc.Migrations.Sqlite;

public sealed class SqliteDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<StructaDocDbContext>
{
    public StructaDocDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=structadoc-design.db",
            sqlite => sqlite.MigrationsAssembly(typeof(SqliteDesignTimeDbContextFactory).Assembly));

        return new StructaDocDbContext(optionsBuilder.Options);
    }
}
