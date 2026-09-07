namespace AiAssistant.Api.Data.Entities;

// Which chunks fed one specific answer, in rank order. Separate from
// Question.ContextUsed (the flattened prompt text) because trace/citation UI
// needs the individual sources, not a blob to re-parse.
public class RequestChunk
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public int ContentChunkId { get; set; }
    public ContentChunk ContentChunk { get; set; } = null!;

    public required float Score { get; set; }
    public required int Rank { get; set; }
}
