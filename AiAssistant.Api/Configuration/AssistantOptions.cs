namespace AiAssistant.Api.Configuration;

public class AssistantOptions
{
    public const string SectionName = "Assistant";

    // The only thing that changes between domains (ticket support, legal
    // advisor, ...). Same binary, different prompt.
    public string SystemPrompt { get; set; } =
        "Siz — foydalanuvchiga faqat berilgan kontekst (hujjatlar va baza ma'lumotlari) " +
        "asosida javob beradigan yordamchisiz.";

    public int GuestQuestionLimit { get; set; } = 5;

    // The exact phrase SystemPrompt instructs the model to use when nothing
    // relevant was found. Checked against the finished answer (see
    // ChatOrchestrator) to tell "retrieval found something, and the model
    // used it" apart from "retrieval found something irrelevant, and the
    // model correctly said so". If a deployment writes its own SystemPrompt
    // for a different domain, this must be updated to match whatever phrase
    // that prompt actually asks for - otherwise every declined answer looks
    // like a grounded one to the client.
    public string NotFoundPhrase { get; set; } = "Bu haqida ma'lumot topilmadi";

    // Capped server-side regardless of what a client sends: every extra turn
    // costs prompt tokens twice over (once in the history-resolution call,
    // once in generation) inside a fixed ChatContextTokens budget shared with
    // the retrieved chunks - an unbounded history would eventually crowd out
    // the context that actually answers the question. Four turns is enough
    // for the immediate-follow-up case ("u qachon bo'lgan?") this exists for,
    // without eating deeply into that budget.
    public int MaxHistoryTurns { get; set; } = 4;
}
