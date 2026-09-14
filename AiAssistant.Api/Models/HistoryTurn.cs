namespace AiAssistant.Api.Models;

// One prior exchange in the same conversation, as the client already has it -
// there is no server-side session store this reads from (see
// ChatOrchestrator's remarks on why), so both sides of every turn are sent up
// with each new question.
public record HistoryTurn(string Question, string Answer);
