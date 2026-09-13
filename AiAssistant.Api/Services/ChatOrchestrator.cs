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
    QuestionLog questionLog,
    SysParamCache sysParams,
    InlineLinkIngestor linkIngestor,
    IOptions<AssistantOptions> assistantOptions,
    IOptions<RagOptions> ragOptions)
{
    private readonly AssistantOptions _assistant = assistantOptions.Value;
    private readonly RagOptions _rag = ragOptions.Value;

    public async IAsyncEnumerable<AnswerEvent> AskAsync(
        string question,
        RetrievalScope? scope = null,
        string? userId = null,
        string? guestKey = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A link pasted directly into the question lands in the asker's own
        // scope, not the shared knowledge base - falls back to a per-user
        // scope when there's no ticket, so an anonymous guest's link doesn't
        // land there either.
        var effectiveScope = scope
            ?? (userId is not null ? RetrievalScope.ForTicket($"user:{userId}") : RetrievalScope.Global);
        await linkIngestor.IngestAnyLinkAsync(question, effectiveScope, ct);

        // Pre-retrieval: slang normalisation, then optional expansion into
        // several phrasings. Retrieval searches all of them and merges.
        var phrasings = await preprocessor.PrepareAsync(question, ct);
        var retrieval = await index.SearchAsync(phrasings, effectiveScope, ct);

        var systemPrompt = _assistant.SystemPrompt;
        var messages = new[]
        {
            ("system", systemPrompt),
            ("user", BuildUserMessage(question, retrieval))
        };

        // Written before generation starts, not after: on a slow host the
        // client (browser, curl, a proxy) can give up before the model does,
        // and a row written only at the end never gets written for exactly
        // the requests worth investigating.
        var questionId = await questionLog.BeginAsync(question, systemPrompt, effectiveScope, userId, guestKey, ct);

        var temperature = await sysParams.GetAsync(SysParamKeys.RagTemperature, _rag.Temperature, ct);

        var answer = new StringBuilder();
        var completed = false;
        AnswerSummary? finalSummary = null;

        try
        {
            await foreach (var token in ollama.StreamChatAsync(messages, temperature, ct))
            {
                answer.Append(token);
                yield return new AnswerEvent.Token(token);
            }
            completed = true;
        }
        finally
        {
            var fullText = answer.ToString();

            // Retrieval finding chunks and the model actually using them are
            // different facts. A greeting can score high enough against an
            // unrelated document to clear the similarity threshold; the model
            // still correctly says it found nothing. Checking the answer text
            // itself is what tells these apart - retrieval.Chunks.Count > 0
            // alone would call the second case "answered from context" too,
            // which is what showed a source citation under a "not found"
            // answer.
            var declined = fullText.Contains(_assistant.NotFoundPhrase, StringComparison.OrdinalIgnoreCase);
            var summary = new AnswerSummary(
                FullText: fullText,
                ContextUsed: retrieval.Chunks,
                LanguageDriftSuspected: UzbekLanguageGuard.LooksLikeRussian(fullText),
                AnsweredFromContext: retrieval.Chunks.Count > 0 && !declined)
            {
                QuestionId = questionId
            };

            var status = completed
                ? (retrieval.IsEmpty ? "no_chunks" : "ok")
                : ct.IsCancellationRequested ? "cancelled" : "gen_error";

            // CancellationToken.None: this write is the point of the whole
            // Begin/Complete split - it must happen even when `ct` is the
            // thing that just got cancelled.
            await questionLog.CompleteAsync(questionId, summary, status, CancellationToken.None);

            if (completed) finalSummary = summary;
        }

        // Only reached on the success path: an exception from the loop above
        // (cancellation or an Ollama failure) propagates past this point
        // after the finally block has already recorded it - the caller's
        // existing catch (OllamaUnavailableException) handles that case.
        yield return new AnswerEvent.Completed(finalSummary!);
    }

    private static string BuildUserMessage(string question, RetrievalResult retrieval) =>
        retrieval.IsEmpty
            ? question
            : $"Kontekst:\n\n{retrieval.PromptContext}\n\n---\n\nSavol: {question}";
}
