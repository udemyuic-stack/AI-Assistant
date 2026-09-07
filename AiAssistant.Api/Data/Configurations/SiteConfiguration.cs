using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.BaseUrl).HasMaxLength(500);
        builder.HasIndex(s => s.Name);
    }
}
