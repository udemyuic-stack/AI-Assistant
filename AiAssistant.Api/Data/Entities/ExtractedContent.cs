namespace AiAssistant.Api.Data.Entities;

// Raw text pulled from one source - a scraped page or an uploaded file -
// before chunking. Kept separately from ContentChunk so re-chunking (a
// different CHUNK_MAX_TOKENS) never re-scrapes or re-uploads.
public class ExtractedContent
{
    public int Id { get; set; }
    public int? TargetPageId { get; set; }
    public TargetPage? TargetPage { get; set; }

    // Set when the source was an upload rather than a scrape.
    public string? SourceFileName { get; set; }
    public string? StorageObjectKey { get; set; }

    public required string Text { get; set; }
    public string? Language { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ContentChunk> Chunks { get; set; } = [];
}
