using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiAssistant.Api.Data.Configurations;

public class SysParamConfiguration : IEntityTypeConfiguration<SysParam>
{
    public void Configure(EntityTypeBuilder<SysParam> builder)
    {
        builder.HasKey(p => p.Key);
        builder.Property(p => p.Key).HasMaxLength(100);
    }
}
