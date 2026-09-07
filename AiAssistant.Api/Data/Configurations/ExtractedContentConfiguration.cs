using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class ExtractedContentConfiguration : IEntityTypeConfiguration<ExtractedContent>
{
    public void Configure(EntityTypeBuilder<ExtractedContent> builder)
    {
        builder.Property(c => c.SourceFileName).HasMaxLength(500);
        builder.Property(c => c.StorageObjectKey).HasMaxLength(1000);
        builder.Property(c => c.Language).HasMaxLength(10);

        builder.HasOne(c => c.TargetPage)
            .WithMany(p => p.ExtractedContents)
            .HasForeignKey(c => c.TargetPageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
