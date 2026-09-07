using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    private static readonly string[] ValidStatuses = ["ok", "no_chunks", "embed_error", "gen_error"];

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
