namespace AiAssistant.Api.Models;

// A completed answer plus everything needed to audit it: which chunks fed the
// prompt, and whether the output drifted out of Uzbek. The orchestrator
// assembles this itself so callers never have to accumulate the token stream
// to find out what happened.
public record AnswerSummary(
    string FullText,
    IReadOnlyList<RetrievedChunk> ContextUsed,
    bool LanguageDriftSuspected)
{
    public bool AnsweredFromContext => ContextUsed.Count > 0;
}

public abstract record AnswerEvent
{
    // One token as the model emits it.
    public sealed record Token(string Text) : AnswerEvent;

    // Always the last event of a successful stream.
    public sealed record Completed(AnswerSummary Summary) : AnswerEvent;
}
