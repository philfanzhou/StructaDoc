using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using StructaDoc.Adapters.ControlPlane;

namespace StructaDoc.Migrations.Sqlite;

/// <summary>
/// The control plane is SQLite only, so its migrations live beside the SQLite business migrations
/// rather than in a provider set of their own.
/// </summary>
public sealed class ControlPlaneDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=structadoc-control-design.db",
            sqlite => sqlite.MigrationsAssembly(
                typeof(ControlPlaneDesignTimeDbContextFactory).Assembly));

        return new ControlPlaneDbContext(optionsBuilder.Options);
    }
}
