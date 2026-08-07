using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Application.Authentication;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.Configurations;

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
        builder.Property(document => document.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(255);
        builder.Property(document => document.OwnerIssuer)
            .HasColumnName("owner_issuer")
            .HasMaxLength(ExternalIdentityConstraints.MaximumIssuerLength);
        builder.Property(document => document.OwnerSubject)
            .HasColumnName("owner_subject")
            .HasMaxLength(ExternalIdentityConstraints.MaximumSubjectLength);
        builder.Property(document => document.LifecycleState)
            .HasColumnName("lifecycle_state")
            .HasMaxLength(32)
            .IsUnicode(false);
        builder.Property(document => document.DeletionRequestedAtUtc)
            .HasColumnName("deletion_requested_at_utc");
        builder.Property(document => document.CreatedAtUtc)
            .HasColumnName("created_at_utc");
        builder.Property(document => document.MetadataJson)
            .HasColumnName("metadata_json");

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
    }
}
