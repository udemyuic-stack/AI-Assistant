namespace AiAssistant.Api.Data.Entities;

// The vector itself lives in Qdrant; this row is the chunk's metadata and
// audit trail - which document it came from, what scope it belongs to, and
// the QdrantPointId that ties the two stores together. Deleting a chunk means
// deleting both rows, in the same order the API already writes them
// (DocumentIndex.IndexFileAsync writes Qdrant; this row records the result).
public class ContentChunk
{
    public int Id { get; set; }
    public int ExtractedContentId { get; set; }
    public ExtractedContent ExtractedContent { get; set; } = null!;

    public required ulong QdrantPointId { get; set; }
    public required string Text { get; set; }
    public required string Scope { get; set; }
    public string? Language { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RequestChunk> RequestChunks { get; set; } = [];
}
