using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Platform.Persistence.Entities;

namespace StructaDoc.Platform.Persistence.Configurations;

internal sealed class ProviderConfigVersionEntityConfiguration
    : IEntityTypeConfiguration<ProviderConfigVersionEntity>
{
    public void Configure(EntityTypeBuilder<ProviderConfigVersionEntity> builder)
    {
        builder.ToTable("provider_config_versions");
        builder.HasKey(version => version.Id);

        builder.Property(version => version.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(version => version.ProviderConfigId).HasColumnName("provider_config_id");
        builder.Property(version => version.VersionNumber).HasColumnName("version_number");
        builder.Property(version => version.BaseUrl)
            .HasColumnName("base_url")
            .HasMaxLength(2048);
        builder.Property(version => version.Model).HasColumnName("model").HasMaxLength(255);
        builder.Property(version => version.Backend).HasColumnName("backend").HasMaxLength(255);
        builder.Property(version => version.ProtectedCredential)
            .HasColumnName("protected_credential")
            .HasMaxLength(8192);
        builder.Property(version => version.CreatedAtUtc).HasColumnName("created_at_utc");

        builder.HasOne(version => version.ProviderConfig)
            .WithMany(config => config.Versions)
            .HasForeignKey(version => version.ProviderConfigId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(version => new { version.ProviderConfigId, version.VersionNumber })
            .IsUnique()
            .HasDatabaseName("ux_provider_config_versions_number");
    }
}
