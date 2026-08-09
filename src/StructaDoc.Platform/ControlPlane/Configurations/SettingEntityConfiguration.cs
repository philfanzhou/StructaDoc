using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Platform.ControlPlane.Entities;

namespace StructaDoc.Platform.ControlPlane.Configurations;

internal sealed class SettingEntityConfiguration : IEntityTypeConfiguration<SettingEntity>
{
    public void Configure(EntityTypeBuilder<SettingEntity> builder)
    {
        builder.ToTable("settings");
        builder.HasKey(setting => setting.Key);

        builder.Property(setting => setting.Key)
            .HasColumnName("key")
            .HasMaxLength(200)
            .IsUnicode(false);
        builder.Property(setting => setting.Value)
            .HasColumnName("value")
            .HasMaxLength(4000);
        builder.Property(setting => setting.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(setting => setting.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(200);
    }
}
