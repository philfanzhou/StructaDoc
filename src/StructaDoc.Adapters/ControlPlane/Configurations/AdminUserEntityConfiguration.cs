using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Application.Authentication;
using StructaDoc.Adapters.ControlPlane.Entities;

namespace StructaDoc.Adapters.ControlPlane.Configurations;

internal sealed class AdminUserEntityConfiguration : IEntityTypeConfiguration<AdminUserEntity>
{
    public void Configure(EntityTypeBuilder<AdminUserEntity> builder)
    {
        builder.ToTable("admin_users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(user => user.Username)
            .HasColumnName("username")
            .HasMaxLength(AdministratorUsernamePolicy.MaximumLength)
            .IsUnicode(false);
        builder.Property(user => user.NormalizedUsername)
            .HasColumnName("normalized_username")
            .HasMaxLength(AdministratorUsernamePolicy.MaximumLength)
            .IsUnicode(false);
        builder.Property(user => user.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(255);
        builder.Property(user => user.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(1024)
            .IsUnicode(false);
        builder.Property(user => user.IsActive).HasColumnName("is_active");
        builder.Property(user => user.SecurityStamp).HasColumnName("security_stamp");
        builder.Property(user => user.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(user => user.LastLoginAtUtc).HasColumnName("last_login_at_utc");

        // First-run claim relies on this index to reject a concurrent second claim, so it must stay
        // unique rather than being enforced by a read-then-write check in application code.
        builder.HasIndex(user => user.NormalizedUsername)
            .IsUnique()
            .HasDatabaseName("ux_admin_users_normalized_username");
    }
}
