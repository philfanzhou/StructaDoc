using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Infrastructure.ControlPlane.Entities;

namespace StructaDoc.Infrastructure.ControlPlane.Configurations;

internal sealed class SetupClaimEntityConfiguration : IEntityTypeConfiguration<SetupClaimEntity>
{
    public void Configure(EntityTypeBuilder<SetupClaimEntity> builder)
    {
        builder.ToTable("setup_claims");
        builder.HasKey(claim => claim.Id);

        builder.Property(claim => claim.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(claim => claim.AdministratorId).HasColumnName("administrator_id");
        builder.Property(claim => claim.ClaimedFromAddress)
            .HasColumnName("claimed_from_address")
            .HasMaxLength(45)
            .IsUnicode(false);
        builder.Property(claim => claim.ClaimedAtUtc).HasColumnName("claimed_at_utc");
        builder.Property(claim => claim.AcknowledgedAtUtc).HasColumnName("acknowledged_at_utc");
    }
}
