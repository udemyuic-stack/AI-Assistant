using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class TargetPageConfiguration : IEntityTypeConfiguration<TargetPage>
{
    public void Configure(EntityTypeBuilder<TargetPage> builder)
    {
        builder.Property(p => p.Url).HasMaxLength(1000);

        builder.HasOne(p => p.Site)
            .WithMany(s => s.TargetPages)
            .HasForeignKey(p => p.SiteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
