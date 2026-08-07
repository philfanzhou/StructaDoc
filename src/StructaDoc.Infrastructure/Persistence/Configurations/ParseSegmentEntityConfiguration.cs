using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.Configurations;

internal sealed class ParseSegmentEntityConfiguration : IEntityTypeConfiguration<ParseSegmentEntity>
{
    public void Configure(EntityTypeBuilder<ParseSegmentEntity> builder)
    {
        builder.ToTable("parse_segments");
        builder.HasKey(segment => segment.Id);
        builder.Property(segment => segment.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(segment => segment.ParseRunId).HasColumnName("parse_run_id");
        builder.Property(segment => segment.Index).HasColumnName("segment_index");
        builder.Property(segment => segment.StartPage).HasColumnName("start_page");
        builder.Property(segment => segment.EndPage).HasColumnName("end_page");
        builder.Property(segment => segment.StorageRef).HasColumnName("storage_ref").HasMaxLength(2048);
        builder.Property(segment => segment.SizeBytes).HasColumnName("size_bytes");
        builder.Property(segment => segment.Sha256).HasColumnName("sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(segment => segment.Status).HasColumnName("status").HasMaxLength(32).IsUnicode(false);
        builder.Property(segment => segment.ExternalTaskId).HasColumnName("external_task_id").HasMaxLength(512);
        builder.Property(segment => segment.ProtectedSubmissionContinuation).HasColumnName("protected_submission_continuation");
        builder.Property(segment => segment.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasOne(segment => segment.ParseRun).WithMany(run => run.Segments).HasForeignKey(segment => segment.ParseRunId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(segment => new { segment.ParseRunId, segment.Index }).IsUnique().HasDatabaseName("ux_parse_segments_index");
    }
}
