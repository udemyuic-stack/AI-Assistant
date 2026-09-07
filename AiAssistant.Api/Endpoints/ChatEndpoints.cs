using System.Text.Json;
using AiAssistant.Api.Models;
using AiAssistant.Api.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace AiAssistant.Api.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        // Server-sent events. Every event is one JSON object: {"token": "..."}
        // while the answer streams, then a final {"done": true, ...} carrying
        // the sources used, so the client can render citations without a
        // second request.
        app.MapPost("/api/chat/stream", async (
            ChatRequest request,
            ChatOrchestrator orchestrator,
            ILogger<Program> logger,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                await Results.BadRequest(new { error = "Savol matni bo'sh." }).ExecuteAsync(http);
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            try
            {
                await foreach (var chunk in orchestrator.AskAsync(request.Message, request.Scope(), ct))
                    await WriteAsync(http, logger, request, chunk, ct);
            }
            catch (OllamaUnavailableException ex)
            {
                logger.LogError(ex, "Ollama javob bermadi");
                await WriteEventAsync(http, new { error = "Model hozir mavjud emas. Keyinroq urinib ko'ring." }, ct);
            }
        }).RequireRateLimiting("ollama");
    }

    private static async Task WriteAsync(
        HttpContext http, ILogger logger, ChatRequest request, AnswerEvent chunk, CancellationToken ct)
    {
        switch (chunk)
        {
            case AnswerEvent.Token token:
                await WriteEventAsync(http, new { token = token.Text }, ct);
                break;

            case AnswerEvent.Completed completed:
                var summary = completed.Summary;
                if (summary.LanguageDriftSuspected)
                    logger.LogWarning(
                        "Javob o'zbek tilidan chetlashgan bo'lishi mumkin. Scope={Scope}",
                        request.TicketScope ?? RetrievalScope.GlobalValue);

                await WriteEventAsync(http, new
                {
                    done = true,
                    answeredFromContext = summary.AnsweredFromContext,
                    sources = summary.ContextUsed.Select(c => c.Source).Distinct()
                }, ct);
                break;
        }
    }

    private static async Task WriteEventAsync(HttpContext http, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}

public record ChatRequest(string Message, string? TicketScope)
{
    public RetrievalScope? Scope() =>
        string.IsNullOrWhiteSpace(TicketScope) ? null : RetrievalScope.ForTicket(TicketScope);
}
