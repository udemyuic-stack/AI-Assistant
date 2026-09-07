using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Data;

// Shares TicketSystem's physical Postgres database but owns its own schema
// ("ai") and migration history, so the two never fight over a table name and
// this context can never touch a TicketSystem-owned table by accident.
//
// TicketSystem's own tables (Tickets, Messages, Users) are read through a
// separate, read-only connection where that context needs them - never
// mapped as entities here. Read-only at the database role level, not just by
// convention: see docs/data-access.md.
public class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<TargetPage> TargetPages => Set<TargetPage>();
    public DbSet<ExtractedContent> ExtractedContents => Set<ExtractedContent>();
    public DbSet<ContentChunk> ContentChunks => Set<ContentChunk>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<RequestChunk> RequestChunks => Set<RequestChunk>();
    public DbSet<FeedbackMark> FeedbackMarks => Set<FeedbackMark>();
    public DbSet<Rubric> Rubrics => Set<Rubric>();
    public DbSet<SysParam> SysParams => Set<SysParam>();
    public DbSet<SlangTerm> SlangTerms => Set<SlangTerm>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ai");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AiDbContext).Assembly);
    }
}
