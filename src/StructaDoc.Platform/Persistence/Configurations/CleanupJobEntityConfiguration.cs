using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Platform.Persistence.Entities;

namespace StructaDoc.Platform.Persistence.Configurations;

internal sealed class CleanupJobEntityConfiguration : IEntityTypeConfiguration<CleanupJobEntity>
{
    public void Configure(EntityTypeBuilder<CleanupJobEntity> builder)
    {
        builder.ToTable("cleanup_jobs");
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(job => job.TargetType).HasColumnName("target_type").HasMaxLength(32).IsUnicode(false);
        builder.Property(job => job.TargetId).HasColumnName("target_id");
        builder.Property(job => job.StorageRefsJson).HasColumnName("storage_refs_json");
        builder.Property(job => job.Status).HasColumnName("status").HasMaxLength(32).IsUnicode(false);
        builder.Property(job => job.AttemptCount).HasColumnName("attempt_count");
        builder.Property(job => job.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
        builder.Property(job => job.ErrorMessage).HasColumnName("error_message").HasMaxLength(2048);
        builder.Property(job => job.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(job => job.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(job => job.ConcurrencyVersion).HasColumnName("concurrency_version").IsConcurrencyToken();
        builder.HasIndex(job => new { job.TargetType, job.TargetId })
            .IsUnique()
            .HasDatabaseName("ux_cleanup_jobs_target");
        builder.HasIndex(job => new { job.Status, job.NextAttemptAtUtc })
            .HasDatabaseName("ix_cleanup_jobs_due");
    }
}
