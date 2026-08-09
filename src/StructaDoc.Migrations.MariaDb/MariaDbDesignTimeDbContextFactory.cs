using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using StructaDoc.Adapters.Persistence;

namespace StructaDoc.Migrations.MariaDb;

public sealed class MariaDbDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<StructaDocDbContext>
{
    public StructaDocDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>();
        optionsBuilder.UseMySql(
            "Server=localhost;Database=structadoc;User=structadoc;Password=unused",
            new MariaDbServerVersion(new Version(11, 4, 0)),
            mariaDb => mariaDb.MigrationsAssembly(typeof(MariaDbDesignTimeDbContextFactory).Assembly));

        return new StructaDocDbContext(optionsBuilder.Options);
    }
}
