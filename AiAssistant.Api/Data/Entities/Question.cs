namespace AiAssistant.Api.Data.Entities;

// One question-answer turn. UserId/TicketId are loose references into
// TicketSystem's own tables (read via the shared connection, not a foreign
// key) - this schema must not depend on TicketSystem's migration history.
public class Question
{
    public int Id { get; set; }

    public string? TicketScope { get; set; }
    public int? UserId { get; set; }

    // Set only when UserId is null - the key an anonymous visitor's question
    // budget is counted against (client IP today; see GuestLimiter). Null for
    // authenticated questions, where TicketSystem's own login is the identity
    // and no separate budget applies.
    public string? GuestKey { get; set; }

    public required string QuestionText { get; set; }
    public string? AnswerText { get; set; }

    // What actually reached the model - the resolved system prompt and the
    // joined context string. Written even when nothing was found, so "why did
    // it say that" is answerable from this row alone (book: system_prompt_used
    // / context_used, called out as the fix for a trace that lied).
    public required string SystemPromptUsed { get; set; }
    public string? ContextUsed { get; set; }

    public required string Status { get; set; } // ok | no_chunks | embed_error | gen_error
    public bool LanguageDriftSuspected { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RequestChunk> RequestChunks { get; set; } = [];
    public FeedbackMark? Feedback { get; set; }
}
