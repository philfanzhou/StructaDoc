using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Platform.Persistence.Entities;

namespace StructaDoc.Platform.Persistence.Configurations;

internal sealed class ApiClientEntityConfiguration : IEntityTypeConfiguration<ApiClientEntity>
{
    public void Configure(EntityTypeBuilder<ApiClientEntity> builder)
    {
        builder.ToTable("api_clients");
        builder.HasKey(client => client.Id);

        builder.Property(client => client.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(client => client.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(client => client.SecretHash)
            .HasColumnName("secret_hash")
            .HasMaxLength(32)
            .IsFixedLength();
        builder.Property(client => client.Scopes)
            .HasColumnName("scopes")
            .HasMaxLength(512)
            .IsUnicode(false);
        builder.Property(client => client.IsActive).HasColumnName("is_active");
        builder.Property(client => client.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(client => client.RevokedAtUtc).HasColumnName("revoked_at_utc");
        builder.Property(client => client.ConcurrencyVersion)
            .HasColumnName("concurrency_version")
            .IsConcurrencyToken();
    }
}
