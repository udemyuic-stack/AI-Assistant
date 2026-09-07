using System.Runtime.CompilerServices;
using System.Text;
using AiAssistant.Api.Configuration;
using AiAssistant.Api.Models;
using Microsoft.Extensions.Options;

namespace AiAssistant.Api.Services;

// Retrieval plus generation behind a single call. The stream ends with a
// Completed event carrying the full text, the chunks that fed the prompt, and
// the language check - so a caller forwards events and never has to reassemble
// the answer to find out what happened.
//
// The system prompt comes from configuration, which is the whole reason this
// service can serve ticket support and, unchanged, a legal assistant.
public class ChatOrchestrator(
    OllamaClient ollama,
    DocumentIndex index,
    IOptions<AssistantOptions> assistantOptions,
    IOptions<RagOptions> ragOptions)
{
    private readonly AssistantOptions _assistant = assistantOptions.Value;
    private readonly RagOptions _rag = ragOptions.Value;

    public async IAsyncEnumerable<AnswerEvent> AskAsync(
        string question,
        RetrievalScope? scope = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var retrieval = await index.SearchAsync(question, scope, ct);

        var messages = new[]
        {
            ("system", _assistant.SystemPrompt),
            ("user", BuildUserMessage(question, retrieval))
        };

        var answer = new StringBuilder();

        await foreach (var token in ollama.StreamChatAsync(messages, _rag.Temperature, ct))
        {
            answer.Append(token);
            yield return new AnswerEvent.Token(token);
        }

        var fullText = answer.ToString();
        yield return new AnswerEvent.Completed(new AnswerSummary(
            FullText: fullText,
            ContextUsed: retrieval.Chunks,
            LanguageDriftSuspected: UzbekLanguageGuard.LooksLikeRussian(fullText)));
    }

    private static string BuildUserMessage(string question, RetrievalResult retrieval) =>
        retrieval.IsEmpty
            ? question
            : $"Kontekst:\n\n{retrieval.PromptContext}\n\n---\n\nSavol: {question}";
}
