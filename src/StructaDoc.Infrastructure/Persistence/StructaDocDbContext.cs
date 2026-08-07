using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence;

public sealed class StructaDocDbContext(DbContextOptions<StructaDocDbContext> options)
    : DbContext(options)
{
    public DbSet<AdminUserEntity> AdminUsers => Set<AdminUserEntity>();

    public DbSet<ApiClientEntity> ApiClients => Set<ApiClientEntity>();

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    public DbSet<DocumentAccessGrantEntity> DocumentAccessGrants => Set<DocumentAccessGrantEntity>();

    public DbSet<CleanupJobEntity> CleanupJobs => Set<CleanupJobEntity>();

    public DbSet<ParseRunEntity> ParseRuns => Set<ParseRunEntity>();

    public DbSet<ParsePageEntity> ParsePages => Set<ParsePageEntity>();

    public DbSet<ParseBlockEntity> ParseBlocks => Set<ParseBlockEntity>();

    public DbSet<ParseAssetEntity> ParseAssets => Set<ParseAssetEntity>();

    public DbSet<ParseArtifactEntity> ParseArtifacts => Set<ParseArtifactEntity>();

    public DbSet<ParseSegmentEntity> ParseSegments => Set<ParseSegmentEntity>();

    public DbSet<ProviderConfigEntity> ProviderConfigs => Set<ProviderConfigEntity>();

    public DbSet<ProviderConfigVersionEntity> ProviderConfigVersions =>
        Set<ProviderConfigVersionEntity>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        IncrementConcurrencyVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        IncrementConcurrencyVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StructaDocDbContext).Assembly);
        ConfigureMySqlOidcIdentityColumns(modelBuilder);
        ConfigureUtcDateTimes(modelBuilder);
    }

    private void ConfigureMySqlOidcIdentityColumns(ModelBuilder modelBuilder)
    {
        if (Database.ProviderName?.EndsWith(
                ".MySql",
                StringComparison.Ordinal) is not true)
        {
            return;
        }

        modelBuilder.Entity<DocumentEntity>()
            .Property(document => document.OwnerIssuer)
            .HasCharSet("ascii")
            .UseCollation("ascii_bin");
        modelBuilder.Entity<DocumentEntity>()
            .Property(document => document.OwnerSubject)
            .HasCharSet("ascii")
            .UseCollation("ascii_bin");
        modelBuilder.Entity<DocumentAccessGrantEntity>()
            .Property(grant => grant.PrincipalIssuer)
            .HasCharSet("ascii")
            .UseCollation("ascii_bin");
        modelBuilder.Entity<DocumentAccessGrantEntity>()
            .Property(grant => grant.PrincipalSubject)
            .HasCharSet("ascii")
            .UseCollation("ascii_bin");
    }

    private void IncrementConcurrencyVersions()
    {
        foreach (var entry in ChangeTracker.Entries<ApiClientEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.ConcurrencyVersion++;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ParseRunEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.ConcurrencyVersion++;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ProviderConfigEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.ConcurrencyVersion++;
            }
        }

        foreach (var entry in ChangeTracker.Entries<CleanupJobEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.ConcurrencyVersion++;
            }
        }
    }

    private static void ConfigureUtcDateTimes(ModelBuilder modelBuilder)
    {
        var dateTimeConverter = new ValueConverter<DateTime, DateTime>(
            value => RequireUtc(value),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        var nullableDateTimeConverter = new ValueConverter<DateTime?, DateTime?>(
            value => value.HasValue ? RequireUtc(value.Value) : value,
            value => value.HasValue
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : value);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(dateTimeConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(nullableDateTimeConverter);
                }
            }
        }
    }

    private static DateTime RequireUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : throw new InvalidOperationException("Persisted DateTime values must use UTC.");
    }
}
