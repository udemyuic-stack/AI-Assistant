namespace AiAssistant.Api.Data.Entities;

// A source (a ministry website, a document collection) that pages/files get
// attached to. Kept even though ingestion today is mostly manual upload,
// because scraping (book chapter 8+) attaches here without a schema change.
public class Site
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? BaseUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TargetPage> TargetPages { get; set; } = [];
}
