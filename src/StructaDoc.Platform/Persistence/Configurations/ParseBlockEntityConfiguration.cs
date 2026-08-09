using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Platform.Persistence.Entities;

namespace StructaDoc.Platform.Persistence.Configurations;

internal sealed class ParseBlockEntityConfiguration : IEntityTypeConfiguration<ParseBlockEntity>
{
    public void Configure(EntityTypeBuilder<ParseBlockEntity> builder)
    {
        builder.ToTable("parse_blocks");
        builder.HasKey(block => block.Id);

        builder.Property(block => block.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(block => block.ParseRunId).HasColumnName("parse_run_id");
        builder.Property(block => block.Sequence).HasColumnName("sequence");
        builder.Property(block => block.PageNumber).HasColumnName("page_number");
        builder.Property(block => block.Type).HasColumnName("type").HasMaxLength(64).IsUnicode(false);
        builder.Property(block => block.Subtype).HasColumnName("subtype").HasMaxLength(100).IsUnicode(false);
        builder.Property(block => block.Content).HasColumnName("content");
        builder.Property(block => block.ContentFormat).HasColumnName("content_format").HasMaxLength(32).IsUnicode(false);
        builder.Property(block => block.BoundingBoxX0).HasColumnName("bbox_x0");
        builder.Property(block => block.BoundingBoxY0).HasColumnName("bbox_y0");
        builder.Property(block => block.BoundingBoxX1).HasColumnName("bbox_x1");
        builder.Property(block => block.BoundingBoxY1).HasColumnName("bbox_y1");
        builder.Property(block => block.Confidence).HasColumnName("confidence");
        builder.Property(block => block.AssetId).HasColumnName("asset_id");
        builder.Property(block => block.SourceLocatorJson).HasColumnName("source_locator_json");
        builder.Property(block => block.ProviderDataJson).HasColumnName("provider_data_json");

        builder.HasOne(block => block.ParseRun)
            .WithMany(parseRun => parseRun.Blocks)
            .HasForeignKey(block => block.ParseRunId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(block => block.Asset)
            .WithMany(asset => asset.Blocks)
            .HasForeignKey(block => new { block.ParseRunId, block.AssetId })
            .HasPrincipalKey(asset => new { asset.ParseRunId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ParsePageEntity>()
            .WithMany()
            .HasForeignKey(block => new { block.ParseRunId, block.PageNumber })
            .HasPrincipalKey(page => new { page.ParseRunId, page.Number })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(block => new { block.ParseRunId, block.Sequence })
            .IsUnique()
            .HasDatabaseName("ux_parse_blocks_sequence");
        builder.HasIndex(block => new { block.ParseRunId, block.PageNumber })
            .HasDatabaseName("ix_parse_blocks_page");
        builder.HasIndex(block => new { block.ParseRunId, block.AssetId })
            .HasDatabaseName("ix_parse_blocks_asset");
    }
}
