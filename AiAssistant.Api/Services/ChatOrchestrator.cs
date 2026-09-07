using System.Runtime.CompilerServices;

namespace AiAssistant.Api.Services;

// Ties retrieval + generation together. System prompt is config-driven so the
// same binary serves different domains (ticket support, legal advisor, etc.)
// - only appsettings/env changes between deployments, no code fork.
public class ChatOrchestrator(OllamaService ollama, QdrantRagService rag, IConfiguration config)
{
    private readonly string _systemPrompt = config["Assistant:SystemPrompt"]
        ?? "Siz — foydalanuvchiga faqat berilgan kontekst (hujjatlar va baza ma'lumotlari) asosida javob beradigan yordamchisiz.";
    private readonly double _temperature = double.Parse(config["Rag:Temperature"] ?? "0.0");

    // Cyrillic letters that only appear in Russian, never in Uzbek Cyrillic
    // or Latin script. A generated answer containing these almost certainly
    // drifted into Russian - the caller should retry with a stronger nudge.
    private static readonly char[] RussianOnlyLetters = ['ы', 'э', 'ъ', 'ё'];

    public async IAsyncEnumerable<string> AskAsync(
        string userMessage,
        string? ticketScope = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var context = await rag.SearchAsync(userMessage, ticketScope);

        var userContent = string.IsNullOrEmpty(context)
            ? userMessage
            : $"Hujjatlardan olingan kontekst:\n\n{context}\n\n---\n\nSavol: {userMessage}";

        var requestMessages = new[]
        {
            ("system", _systemPrompt),
            ("user",   userContent)
        };

        await foreach (var chunk in ollama.ChatStreamAsync(requestMessages, _temperature, ct))
            yield return chunk;
    }

    // Cheap post-hoc guard: true if the text looks like it drifted into
    // Russian instead of Uzbek. Callers decide what to do (retry, flag, log).
    public static bool LooksLikeRussianDrift(string text) =>
        text.Length > 20 && text.Count(c => RussianOnlyLetters.Contains(c)) > 2;
}
