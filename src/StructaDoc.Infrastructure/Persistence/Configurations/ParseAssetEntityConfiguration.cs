using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.Configurations;

internal sealed class ParseAssetEntityConfiguration : IEntityTypeConfiguration<ParseAssetEntity>
{
    public void Configure(EntityTypeBuilder<ParseAssetEntity> builder)
    {
        builder.ToTable("parse_assets");
        builder.HasKey(asset => asset.Id);
        builder.HasAlternateKey(asset => new { asset.ParseRunId, asset.Id });

        builder.Property(asset => asset.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(asset => asset.ParseRunId).HasColumnName("parse_run_id");
        builder.Property(asset => asset.Name).HasColumnName("name").HasMaxLength(512);
        builder.Property(asset => asset.MediaType).HasColumnName("media_type").HasMaxLength(255);
        builder.Property(asset => asset.SizeBytes).HasColumnName("size_bytes");
        builder.Property(asset => asset.Sha256).HasColumnName("sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(asset => asset.StorageRef).HasColumnName("storage_ref").HasMaxLength(2048);
        builder.Property(asset => asset.Width).HasColumnName("width");
        builder.Property(asset => asset.Height).HasColumnName("height");
        builder.Property(asset => asset.CreatedAtUtc).HasColumnName("created_at_utc");

        builder.HasOne(asset => asset.ParseRun)
            .WithMany(parseRun => parseRun.Assets)
            .HasForeignKey(asset => asset.ParseRunId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(asset => new { asset.ParseRunId, asset.Name })
            .HasDatabaseName("ix_parse_assets_name");
        builder.HasIndex(asset => asset.Sha256)
            .HasDatabaseName("ix_parse_assets_sha256");
    }
}
