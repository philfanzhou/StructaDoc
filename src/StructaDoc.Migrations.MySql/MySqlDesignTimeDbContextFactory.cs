using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using StructaDoc.Platform.Persistence;

namespace StructaDoc.Migrations.MySql;

public sealed class MySqlDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<StructaDocDbContext>
{
    public StructaDocDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();
        optionsBuilder.UseMySql(
            "Server=localhost;Database=structadoc;User=structadoc;Password=unused",
            new MySqlServerVersion(new Version(8, 4, 0)),
            mySql => mySql.MigrationsAssembly(typeof(MySqlDesignTimeDbContextFactory).Assembly));

        return new StructaDocDbContext(optionsBuilder.Options);
    }
}
