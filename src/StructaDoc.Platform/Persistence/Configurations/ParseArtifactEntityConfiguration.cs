using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Platform.Persistence.Entities;

namespace StructaDoc.Platform.Persistence.Configurations;

internal sealed class ParseArtifactEntityConfiguration : IEntityTypeConfiguration<ParseArtifactEntity>
{
    public void Configure(EntityTypeBuilder<ParseArtifactEntity> builder)
    {
        builder.ToTable("parse_artifacts");
        builder.HasKey(artifact => artifact.Id);

        builder.Property(artifact => artifact.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(artifact => artifact.ParseRunId).HasColumnName("parse_run_id");
        builder.Property(artifact => artifact.Type).HasColumnName("type").HasMaxLength(64).IsUnicode(false);
        builder.Property(artifact => artifact.Name).HasColumnName("name").HasMaxLength(512);
        builder.Property(artifact => artifact.MediaType).HasColumnName("media_type").HasMaxLength(255);
        builder.Property(artifact => artifact.SizeBytes).HasColumnName("size_bytes");
        builder.Property(artifact => artifact.Sha256).HasColumnName("sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(artifact => artifact.StorageRef).HasColumnName("storage_ref").HasMaxLength(2048);
        builder.Property(artifact => artifact.MetadataJson).HasColumnName("metadata_json");
        builder.Property(artifact => artifact.CreatedAtUtc).HasColumnName("created_at_utc");

        builder.HasOne(artifact => artifact.ParseRun)
            .WithMany(parseRun => parseRun.Artifacts)
            .HasForeignKey(artifact => artifact.ParseRunId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(artifact => new { artifact.ParseRunId, artifact.Type, artifact.Name })
            .IsUnique()
            .HasDatabaseName("ux_parse_artifacts_key");
        builder.HasIndex(artifact => artifact.Sha256)
            .HasDatabaseName("ix_parse_artifacts_sha256");
    }
}
