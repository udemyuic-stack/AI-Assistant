namespace AiAssistant.Api.Models;

// A completed answer plus everything needed to audit it: which chunks fed the
// prompt, and whether the output drifted out of Uzbek. The orchestrator
// assembles this itself so callers never have to accumulate the token stream
// to find out what happened.
public record AnswerSummary(
    string FullText,
    IReadOnlyList<RetrievedChunk> ContextUsed,
    bool LanguageDriftSuspected,
    // Explicit, not derived from ContextUsed.Count: retrieval can hand back
    // chunks that clear the similarity threshold without being relevant (a
    // greeting scored against an unrelated document, say), and the model
    // correctly answers "not found" anyway. ContextUsed.Count > 0 alone said
    // "answered from context" in that case - true about retrieval, false
    // about the answer. ChatOrchestrator sets this by checking the answer
    // text itself against AssistantOptions.NotFoundPhrase.
    bool AnsweredFromContext)
{
    // Set once the trace row exists. The client needs it to attach feedback
    // to this specific answer.
    public int? QuestionId { get; init; }
}

public abstract record AnswerEvent
{
    // One token as the model emits it.
    public sealed record Token(string Text) : AnswerEvent;

    // Always the last event of a successful stream.
    public sealed record Completed(AnswerSummary Summary) : AnswerEvent;
}
