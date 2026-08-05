using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence;

public sealed class StructaDocDbContext(DbContextOptions<StructaDocDbContext> options)
    : DbContext(options)
{
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    public DbSet<ParseRunEntity> ParseRuns => Set<ParseRunEntity>();

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
        ConfigureUtcDateTimes(modelBuilder);
    }

    private void IncrementConcurrencyVersions()
    {
        foreach (var entry in ChangeTracker.Entries<ParseRunEntity>())
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
