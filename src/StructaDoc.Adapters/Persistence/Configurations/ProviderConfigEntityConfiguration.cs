using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Adapters.Persistence.Entities;

namespace StructaDoc.Adapters.Persistence.Configurations;

internal sealed class ProviderConfigEntityConfiguration
    : IEntityTypeConfiguration<ProviderConfigEntity>
{
    public void Configure(EntityTypeBuilder<ProviderConfigEntity> builder)
    {
        builder.ToTable("provider_configs");
        builder.HasKey(config => config.Id);

        builder.Property(config => config.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(config => config.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(config => config.ProviderType)
            .HasColumnName("provider_type")
            .HasMaxLength(100)
            .IsUnicode(false);
        builder.Property(config => config.IsEnabled).HasColumnName("is_enabled");
        builder.Property(config => config.DefaultMarker)
            .HasColumnName("default_marker")
            .HasMaxLength(16)
            .IsUnicode(false);
        builder.Property(config => config.CurrentVersionId)
            .HasColumnName("current_version_id");
        builder.Property(config => config.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(config => config.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(config => config.ConcurrencyVersion)
            .HasColumnName("concurrency_version")
            .IsConcurrencyToken();

        builder.HasIndex(config => config.DefaultMarker)
            .IsUnique()
            .HasDatabaseName("ux_provider_configs_default_marker");
        builder.HasIndex(config => config.CurrentVersionId)
            .HasDatabaseName("ix_provider_configs_current_version");
    }
}
