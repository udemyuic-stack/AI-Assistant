namespace AiAssistant.Api.Models;

// Which slice of the index a document belongs to, and which slices a question
// may read. Stringly-typed scopes are a data-leak hazard: a typo silently
// widens or narrows what a caller can see, and nothing fails loudly. This type
// makes every call site state its intent.
//
// Note: constructing a ticket scope does NOT prove the caller owns that
// ticket. Ownership is enforced separately, at the service layer.
public readonly record struct RetrievalScope(string Value)
{
    public const string GlobalValue = "global";

    // The shared knowledge base: readable by every question.
    public static RetrievalScope Global => new(GlobalValue);

    // One conversation's own uploads. Never visible to other conversations.
    public static RetrievalScope ForTicket(string ticketId) => new($"ticket:{ticketId}");

    public bool IsGlobal => Value == GlobalValue;

    public override string ToString() => Value;
}
