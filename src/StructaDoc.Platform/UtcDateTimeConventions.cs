using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StructaDoc.Platform;

/// <summary>
/// Applies the repository-wide rule that every persisted <see cref="DateTime"/> is UTC. Shared by
/// the business and control-plane contexts so the two cannot drift apart on timestamp handling.
/// </summary>
internal static class UtcDateTimeConventions
{
    public static void Apply(ModelBuilder modelBuilder)
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
