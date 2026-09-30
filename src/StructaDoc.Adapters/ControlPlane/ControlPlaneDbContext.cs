using Microsoft.EntityFrameworkCore;
using ServiceMantle.Persistence.Relational.Mapping;
using StructaDoc.Adapters.ControlPlane.Entities;

namespace StructaDoc.Adapters.ControlPlane;

/// <summary>
/// The control plane holds what the service needs in order to be administered at all: who may sign
/// in, how the first administrator came to exist, what an administrator has configured through the
/// web interface, and the management audit trail of those actions. It is always a local SQLite
/// file and never moves to the configured business database, because the business database is
/// itself something an administrator configures. Keeping the two separate breaks that cycle, and
/// keeps break-glass sign-in working while the business database is unreachable.
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
        // Management audit rows live here rather than in the business database: the audit has to
        // record database administration itself, so it cannot depend on the thing it audits, and
        // the library's audit mapping does not cover all four business-database dialects anyway.
        // The audit trail therefore joins the existing control-plane backup recovery set.
        modelBuilder.AddServiceMantleManagementAudit(
            ManagementAuditDatabaseDialect.Sqlite);
        UtcDateTimeConventions.Apply(modelBuilder);
    }
}
