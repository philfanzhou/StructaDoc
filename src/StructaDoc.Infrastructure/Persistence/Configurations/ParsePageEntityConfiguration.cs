using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Persistence.Configurations;

internal sealed class ParsePageEntityConfiguration : IEntityTypeConfiguration<ParsePageEntity>
{
    public void Configure(EntityTypeBuilder<ParsePageEntity> builder)
    {
        builder.ToTable("parse_pages");
        builder.HasKey(page => new { page.ParseRunId, page.Number });

        builder.Property(page => page.ParseRunId).HasColumnName("parse_run_id");
        builder.Property(page => page.Number).HasColumnName("number");
        builder.Property(page => page.Width).HasColumnName("width");
        builder.Property(page => page.Height).HasColumnName("height");
        builder.Property(page => page.Unit).HasColumnName("unit").HasMaxLength(32).IsUnicode(false);
        builder.Property(page => page.SourceLocatorJson).HasColumnName("source_locator_json");

        builder.HasOne(page => page.ParseRun)
            .WithMany(parseRun => parseRun.Pages)
            .HasForeignKey(page => page.ParseRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
