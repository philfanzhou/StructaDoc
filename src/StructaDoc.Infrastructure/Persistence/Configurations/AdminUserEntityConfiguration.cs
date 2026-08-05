using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.Configurations;

internal sealed class AdminUserEntityConfiguration : IEntityTypeConfiguration<AdminUserEntity>
{
    public void Configure(EntityTypeBuilder<AdminUserEntity> builder)
    {
        builder.ToTable("admin_users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(user => user.Email).HasColumnName("email").HasMaxLength(320);
        builder.Property(user => user.NormalizedEmail)
            .HasColumnName("normalized_email")
            .HasMaxLength(320);
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

        builder.HasIndex(user => user.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("ux_admin_users_normalized_email");
    }
}
