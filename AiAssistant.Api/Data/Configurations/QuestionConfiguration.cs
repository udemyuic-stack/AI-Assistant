using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    // "pending" is written before generation starts and never reaches the
    // client as a final state - it only survives in the table if the request
    // was cancelled or crashed mid-stream. A row stuck at "pending" is itself
    // the diagnostic: it means a client (browser, curl, load balancer) gave
    // up before the model finished, which the CPU-only host makes common
    // enough that it needs to be visible, not silently lost.
    private static readonly string[] ValidStatuses =
        ["pending", "ok", "no_chunks", "embed_error", "gen_error", "cancelled"];

    public void Configure(EntityTypeBuilder<Question> builder)
    {
        builder.Property(q => q.TicketScope).HasMaxLength(200);
        builder.Property(q => q.Status).HasMaxLength(20);

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Question_Status",
            $"\"Status\" IN ({string.Join(", ", ValidStatuses.Select(s => $"'{s}'"))})"));

        builder.HasIndex(q => q.CreatedAt);
        builder.HasIndex(q => q.TicketScope);
    }
}
