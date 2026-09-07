using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class RequestChunkConfiguration : IEntityTypeConfiguration<RequestChunk>
{
    public void Configure(EntityTypeBuilder<RequestChunk> builder)
    {
        builder.HasOne(rc => rc.Question)
            .WithMany(q => q.RequestChunks)
            .HasForeignKey(rc => rc.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not cascade: a chunk that fed a past answer must not
        // vanish out from under that answer's trace when the source document
        // is deleted. Deleting a chunk's trace history is a deliberate,
        // separate operation.
        builder.HasOne(rc => rc.ContentChunk)
            .WithMany(c => c.RequestChunks)
            .HasForeignKey(rc => rc.ContentChunkId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
