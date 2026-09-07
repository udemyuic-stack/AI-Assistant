using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class SlangTermConfiguration : IEntityTypeConfiguration<SlangTerm>
{
    public void Configure(EntityTypeBuilder<SlangTerm> builder)
    {
        builder.Property(t => t.Term).HasMaxLength(200);
        builder.Property(t => t.CanonicalForm).HasMaxLength(200);
        builder.HasIndex(t => t.Term).IsUnique();
    }
}
