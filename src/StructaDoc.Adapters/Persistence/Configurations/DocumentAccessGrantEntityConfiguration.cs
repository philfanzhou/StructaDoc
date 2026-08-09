using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Application.Authentication;
using StructaDoc.Adapters.Persistence.Entities;

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
        builder.Property(grant => grant.PrincipalIssuer).HasColumnName("principal_issuer").HasMaxLength(ExternalIdentityConstraints.MaximumIssuerLength);
        builder.Property(grant => grant.PrincipalSubject).HasColumnName("principal_subject").HasMaxLength(ExternalIdentityConstraints.MaximumSubjectLength);
        builder.Property(grant => grant.Permissions).HasColumnName("permissions");
        builder.Property(grant => grant.CreatedBy).HasColumnName("created_by").HasMaxLength(1024);
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
    }
}
