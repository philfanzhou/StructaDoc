using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.Configurations;

internal sealed class ParseRunEntityConfiguration : IEntityTypeConfiguration<ParseRunEntity>
{
    public void Configure(EntityTypeBuilder<ParseRunEntity> builder)
    {
        builder.ToTable("parse_runs");

        builder.HasKey(parseRun => parseRun.Id);

        builder.Property(parseRun => parseRun.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(parseRun => parseRun.DocumentId)
            .HasColumnName("document_id");
        builder.Property(parseRun => parseRun.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsUnicode(false);
        builder.Property(parseRun => parseRun.Stage)
            .HasColumnName("stage")
            .HasMaxLength(64)
            .IsUnicode(false);
        builder.Property(parseRun => parseRun.ProviderType)
            .HasColumnName("provider_type")
            .HasMaxLength(100)
            .IsUnicode(false);
        builder.Property(parseRun => parseRun.ProviderConfigId)
            .HasColumnName("provider_config_id");
        builder.Property(parseRun => parseRun.ProviderConfigVersion)
            .HasColumnName("provider_config_version");
        builder.Property(parseRun => parseRun.OptionsJson)
            .HasColumnName("options_json");
        builder.Property(parseRun => parseRun.SourceMediaType)
            .HasColumnName("source_media_type")
            .HasMaxLength(255);
        builder.Property(parseRun => parseRun.SubmittedMediaType)
            .HasColumnName("submitted_media_type")
            .HasMaxLength(255);
        builder.Property(parseRun => parseRun.ConversionJson)
            .HasColumnName("conversion_json");
        builder.Property(parseRun => parseRun.ExternalTaskId)
            .HasColumnName("external_task_id")
            .HasMaxLength(512);
        builder.Property(parseRun => parseRun.AttemptCount)
            .HasColumnName("attempt_count");
        builder.Property(parseRun => parseRun.MaxAttempts)
            .HasColumnName("max_attempts");
        builder.Property(parseRun => parseRun.NextAttemptAtUtc)
            .HasColumnName("next_attempt_at_utc");
        builder.Property(parseRun => parseRun.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(128)
            .IsUnicode(false);
        builder.Property(parseRun => parseRun.ErrorMessage)
            .HasColumnName("error_message")
            .HasMaxLength(2048);
        builder.Property(parseRun => parseRun.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(255);
        builder.Property(parseRun => parseRun.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(256)
            .IsUnicode(false);
        builder.Property(parseRun => parseRun.ClaimedBy)
            .HasColumnName("claimed_by")
            .HasMaxLength(255);
        builder.Property(parseRun => parseRun.LeaseExpiresAtUtc)
            .HasColumnName("lease_expires_at_utc");
        builder.Property(parseRun => parseRun.CreatedAtUtc)
            .HasColumnName("created_at_utc");
        builder.Property(parseRun => parseRun.StartedAtUtc)
            .HasColumnName("started_at_utc");
        builder.Property(parseRun => parseRun.CompletedAtUtc)
            .HasColumnName("completed_at_utc");
        builder.Property(parseRun => parseRun.ConcurrencyVersion)
            .HasColumnName("concurrency_version")
            .IsConcurrencyToken();

        builder.HasOne(parseRun => parseRun.Document)
            .WithMany(document => document.ParseRuns)
            .HasForeignKey(parseRun => parseRun.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(parseRun => parseRun.DocumentId)
            .HasDatabaseName("ix_parse_runs_document_id");
        builder.HasIndex(parseRun => new { parseRun.Status, parseRun.NextAttemptAtUtc })
            .HasDatabaseName("ix_parse_runs_due");
        builder.HasIndex(parseRun => parseRun.LeaseExpiresAtUtc)
            .HasDatabaseName("ix_parse_runs_lease_expiry");
        builder.HasIndex(parseRun => new
        {
            parseRun.CreatedBy,
            parseRun.DocumentId,
            parseRun.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName("ux_parse_runs_idempotency");
    }
}
