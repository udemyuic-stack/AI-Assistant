using System.Text;
using AiAssistant.Api.Models;

namespace AiAssistant.Api.Services;

// Turns a follow-up question into one retrieval can act on alone.
//
// The problem this solves is specific to search, not generation: "u qachon
// bo'lgan?" embeds to something close to the *word* "qachon" and nothing
// about which event the asker means, because the pronoun carries all the
// actual meaning and an embedding model has no conversation to resolve it
// against. The model generating the final answer gets the real history in
// its prompt (see ChatOrchestrator.BuildUserMessage) and could in principle
// work out what "u" refers to on its own - but retrieval runs *before*
// generation, against whatever text gets embedded, so if the search query
// itself doesn't name the thing, the right chunk is never fetched for the
// model to reason over in the first place.
//
// One Ollama call, and only when there is history to resolve against - the
// first question in any conversation pays nothing extra.
public class HistoryContextualizer(OllamaClient ollama, ILogger<HistoryContextualizer> logger)
{
    private const string Instruction =
        "Quyida suhbat tarixi va foydalanuvchining oxirgi savoli berilgan. " +
        "Agar oxirgi savol oldingi suhbatga bog'liq bo'lsa (masalan 'u', 'shu', 'yana' kabi " +
        "so'zlar ishlatilgan bo'lsa), uni tarixdan foydalanib TO'LIQ VA MUSTAQIL savolga aylantiring - " +
        "boshqa hech kim bu suhbatni ko'rmasa ham tushunadigan qilib. Agar oxirgi savol allaqachon " +
        "mustaqil bo'lsa, uni o'zgarishsiz qaytaring. Faqat qayta yozilgan savolni yozing, izohsiz.";

    public async Task<string> ResolveAsync(
        string question, IReadOnlyList<HistoryTurn> history, CancellationToken ct)
    {
        if (history.Count == 0) return question;

        var transcript = new StringBuilder();
        foreach (var turn in history)
            transcript.Append("Savol: ").Append(turn.Question)
                .Append("\nJavob: ").Append(turn.Answer).Append("\n\n");
        transcript.Append("Oxirgi savol: ").Append(question);

        try
        {
            var messages = new[] { ("system", Instruction), ("user", transcript.ToString()) };

            var resolved = new StringBuilder();
            await foreach (var token in ollama.StreamChatAsync(messages, temperature: 0, ct))
                resolved.Append(token);

            var text = resolved.ToString().Trim();
            return text.Length > 2 ? text : question;
        }
        catch (OllamaUnavailableException ex)
        {
            // Same fallback QueryPreprocessor uses for its own Ollama-backed
            // steps: searching the original question is still correct, only
            // narrower for a follow-up whose reference never got resolved.
            logger.LogWarning(ex, "Savolni suhbat tarixi asosida aniqlashtirib bo'lmadi, asl savol ishlatiladi.");
            return question;
        }
    }
}
