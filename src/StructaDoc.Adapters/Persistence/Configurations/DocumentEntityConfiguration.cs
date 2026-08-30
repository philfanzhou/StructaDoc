using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Adapters.Persistence.Configurations;

internal sealed class DocumentEntityConfiguration : IEntityTypeConfiguration<DocumentEntity>
{
    public void Configure(EntityTypeBuilder<DocumentEntity> builder)
    {
        builder.ToTable("documents");

        builder.HasKey(document => document.Id);

        builder.Property(document => document.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(document => document.OriginalFileName)
            .HasColumnName("original_file_name")
            .HasMaxLength(512);
        builder.Property(document => document.MediaType)
            .HasColumnName("media_type")
            .HasMaxLength(255);
        builder.Property(document => document.Extension)
            .HasColumnName("extension")
            .HasMaxLength(32);
        builder.Property(document => document.SizeBytes)
            .HasColumnName("size_bytes");
        builder.Property(document => document.Sha256)
            .HasColumnName("sha256")
            .HasMaxLength(64)
            .IsUnicode(false);
        builder.Property(document => document.StorageRef)
            .HasColumnName("storage_ref")
            .HasMaxLength(2048);
        builder.Property(document => document.CreatedByIssuer)
            .HasColumnName("created_by_issuer")
            .HasMaxLength(CanonicalActorPersistence.MaximumIssuerByteCount);
        builder.Property(document => document.CreatedBySubject)
            .HasColumnName("created_by_subject")
            .HasMaxLength(CanonicalActorPersistence.MaximumSubjectByteCount);
        builder.Property(document => document.CreatedByLegacy)
            .HasColumnName("created_by_legacy")
            .HasMaxLength(CanonicalActorPersistence.MaximumDocumentOrParseRunLegacyByteCount);
        builder.Property(document => document.OwnerIssuer)
            .HasColumnName("owner_issuer")
            .HasMaxLength(CanonicalActorPersistence.MaximumIssuerByteCount);
        builder.Property(document => document.OwnerSubject)
            .HasColumnName("owner_subject")
            .HasMaxLength(CanonicalActorPersistence.MaximumSubjectByteCount);
        builder.Property(document => document.LifecycleState)
            .HasColumnName("lifecycle_state")
            .HasMaxLength(32)
            .IsUnicode(false);
        builder.Property(document => document.DeletionRequestedAtUtc)
            .HasColumnName("deletion_requested_at_utc");
        builder.Property(document => document.ConcurrencyVersion)
            .HasColumnName("concurrency_version")
            .IsConcurrencyToken();
        builder.Property(document => document.CreatedAtUtc)
            .HasColumnName("created_at_utc");
        builder.HasIndex(document => document.Sha256)
            .HasDatabaseName("ix_documents_sha256");
        builder.HasIndex(document => new { document.CreatedAtUtc, document.Id })
            .HasDatabaseName("ix_documents_created_at_id");
        builder.HasIndex(document => new
        {
            document.OwnerIssuer,
            document.OwnerSubject,
            document.CreatedAtUtc,
        })
            .HasDatabaseName("ix_documents_owner_created_at");
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_documents_created_by_state",
            "((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL))"));
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_documents_owner_state",
            "((owner_issuer IS NULL AND owner_subject IS NULL) OR (owner_issuer IS NOT NULL AND owner_subject IS NOT NULL))"));
    }
}
