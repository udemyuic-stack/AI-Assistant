namespace AiAssistant.Api.Models;

public record RetrievedChunk(string Text, string Source, float Score, string Scope);

// Retrieval returns the chunks themselves, not a pre-joined blob. Callers that
// only want prompt text call PromptContext; callers that need to record which
// sources produced an answer (trace, citations, feedback) read Chunks.
// Returning a string here would foreclose both.
public record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks)
{
    public static RetrievalResult Empty { get; } = new([]);

    public bool IsEmpty => Chunks.Count == 0;

    // One place owns the separator convention, so callers never hand-roll it.
    public string PromptContext => string.Join("\n\n---\n\n", Chunks.Select(c => c.Text));

    public IReadOnlyList<string> Sources =>
        Chunks.Select(c => c.Source).Distinct().ToList();
}
