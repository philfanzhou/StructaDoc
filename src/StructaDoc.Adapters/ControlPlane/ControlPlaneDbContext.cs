using Microsoft.EntityFrameworkCore;
using StructaDoc.Adapters.ControlPlane.Entities;

namespace StructaDoc.Adapters.ControlPlane;

/// <summary>
/// The control plane holds what the service needs in order to be administered at all: who may sign
/// in, how the first administrator came to exist, and what an administrator has configured through
/// the web interface. It is always a local SQLite file and never
/// moves to the configured business database, because the business database is itself something an
/// administrator configures. Keeping the two separate breaks that cycle, and keeps break-glass
/// sign-in working while the business database is unreachable.
/// </summary>
public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
    : DbContext(options)
{
    public DbSet<AdminUserEntity> AdminUsers => Set<AdminUserEntity>();

    public DbSet<SetupClaimEntity> SetupClaims => Set<SetupClaimEntity>();

    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ControlPlaneDbContext).Assembly,
            type => type.Namespace?.StartsWith(
                "StructaDoc.Adapters.ControlPlane.Configurations",
                StringComparison.Ordinal) is true);
        UtcDateTimeConventions.Apply(modelBuilder);
    }
}
