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
        // Pre-retrieval: slang normalisation, then optional expansion into
        // several phrasings. Retrieval searches all of them and merges.
        var phrasings = await preprocessor.PrepareAsync(question, ct);
        var retrieval = await index.SearchAsync(phrasings, scope, ct);

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
        var questionId = await questionLog.BeginAsync(question, systemPrompt, scope, userId, guestKey, ct);

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
            var summary = new AnswerSummary(
                FullText: answer.ToString(),
                ContextUsed: retrieval.Chunks,
                LanguageDriftSuspected: UzbekLanguageGuard.LooksLikeRussian(answer.ToString()))
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
