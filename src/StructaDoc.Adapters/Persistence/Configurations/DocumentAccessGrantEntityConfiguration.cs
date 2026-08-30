using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Adapters.Persistence.Configurations;

internal sealed class DocumentAccessGrantEntityConfiguration
    : IEntityTypeConfiguration<DocumentAccessGrantEntity>
{
    public void Configure(EntityTypeBuilder<DocumentAccessGrantEntity> builder)
    {
        builder.ToTable("document_access_grants");
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(grant => grant.DocumentId).HasColumnName("document_id");
        builder.Property(grant => grant.PrincipalIssuer).HasColumnName("principal_issuer").HasMaxLength(CanonicalActorPersistence.MaximumIssuerByteCount);
        builder.Property(grant => grant.PrincipalSubject).HasColumnName("principal_subject").HasMaxLength(CanonicalActorPersistence.MaximumSubjectByteCount);
        builder.Property(grant => grant.Permissions).HasColumnName("permissions");
        builder.Property(grant => grant.CreatedByIssuer).HasColumnName("created_by_issuer").HasMaxLength(CanonicalActorPersistence.MaximumIssuerByteCount);
        builder.Property(grant => grant.CreatedBySubject).HasColumnName("created_by_subject").HasMaxLength(CanonicalActorPersistence.MaximumSubjectByteCount);
        builder.Property(grant => grant.CreatedByLegacy).HasColumnName("created_by_legacy").HasMaxLength(CanonicalActorPersistence.MaximumAccessGrantLegacyByteCount);
        builder.Property(grant => grant.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.HasOne(grant => grant.Document)
            .WithMany(document => document.AccessGrants)
            .HasForeignKey(grant => grant.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(grant => new
        {
            grant.DocumentId,
            grant.PrincipalIssuer,
            grant.PrincipalSubject,
        })
            .IsUnique()
            .HasDatabaseName("ux_document_access_grants_principal");
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_document_access_grants_created_by_state",
            "((created_by_issuer IS NOT NULL AND created_by_subject IS NOT NULL AND created_by_legacy IS NULL) OR (created_by_issuer IS NULL AND created_by_subject IS NULL AND created_by_legacy IS NOT NULL))"));
    }
}
