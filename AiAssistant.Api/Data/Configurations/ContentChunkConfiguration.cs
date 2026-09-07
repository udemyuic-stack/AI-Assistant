using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class ContentChunkConfiguration : IEntityTypeConfiguration<ContentChunk>
{
    public void Configure(EntityTypeBuilder<ContentChunk> builder)
    {
        builder.Property(c => c.Scope).HasMaxLength(200);
        builder.Property(c => c.Language).HasMaxLength(10);

        // The join back from a Qdrant search hit to its audit row.
        builder.HasIndex(c => c.QdrantPointId).IsUnique();
        builder.HasIndex(c => c.Scope);

        builder.HasOne(c => c.ExtractedContent)
            .WithMany(e => e.Chunks)
            .HasForeignKey(c => c.ExtractedContentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
