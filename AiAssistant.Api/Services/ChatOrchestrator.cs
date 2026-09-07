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
    QueryPreprocessor preprocessor,
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
        // Pre-retrieval: slang normalisation, then optional expansion into
        // several phrasings. Retrieval searches all of them and merges.
        var phrasings = await preprocessor.PrepareAsync(question, ct);
        var retrieval = await index.SearchAsync(phrasings, scope, ct);

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

        // Deliberately not auto-retried: tokens already streamed to the caller
        // can't be un-sent, so a real fix means generating the full answer
        // before streaming anything - doubling latency on an already-slow
        // CPU host for a failure mode the system prompt's few-shot examples
        // make rare in practice. Flag it; let the caller decide (re-ask,
        // show a warning, log for the gold set).
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
