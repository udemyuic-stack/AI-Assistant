using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class FeedbackMarkConfiguration : IEntityTypeConfiguration<FeedbackMark>
{
    public void Configure(EntityTypeBuilder<FeedbackMark> builder)
    {
        builder.Property(f => f.Code).HasMaxLength(10);
        builder.ToTable(t => t.HasCheckConstraint("CK_FeedbackMark_Code", "\"Code\" IN ('green', 'red')"));

        // One feedback row per question - a changed rating updates it.
        builder.HasIndex(f => f.QuestionId).IsUnique();

        builder.HasOne(f => f.Question)
            .WithOne(q => q.Feedback)
            .HasForeignKey<FeedbackMark>(f => f.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
