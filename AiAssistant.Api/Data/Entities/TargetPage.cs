namespace AiAssistant.Api.Data.Entities;

public class TargetPage
{
    public int Id { get; set; }
    public int SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string? Url { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ExtractedContent> ExtractedContents { get; set; } = [];
}
