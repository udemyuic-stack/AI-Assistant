using AiAssistant.Api.Models;
using AiAssistant.Api.Services;

namespace AiAssistant.Api.Endpoints;

// Retrieval without generation, for measuring retrieval on its own.
//
// The three levers that decide answer quality - chunk size, similarity
// threshold, and (later) hybrid search - all act before the model is ever
// called. Running a full question to measure them costs a ~60 second gemma4
// generation on this CPU-only host; running retrieval alone costs one
// embedding call, about 0.3 seconds. That is the difference between a tuning
// loop of 15 seconds over a 40-question set and one of 40 minutes, which is
// the difference between trying ten configurations and trying one.
//
// Deliberately calls the real QueryPreprocessor and DocumentIndex rather than
// re-implementing search: a harness that re-implements the thing it measures
// drifts from it, and then reports on a pipeline nobody runs.
public static class EvaluationEndpoints
{
    public static void MapEvaluationEndpoints(this WebApplication app)
    {
        // Admin-only for the same reason the trace endpoint is: the response
        // contains the source material behind other people's scopes whenever
        // one is passed, and an unauthenticated caller could walk scopes.
        app.MapPost("/api/evaluation/retrieval", async (
            RetrievalProbeRequest request,
            QueryPreprocessor preprocessor,
            DocumentIndex index,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return Results.BadRequest(new { error = "Savol matni bo'sh." });

            var scope = string.IsNullOrWhiteSpace(request.Scope)
                ? RetrievalScope.Global
                : new RetrievalScope(request.Scope);

            var phrasings = await preprocessor.PrepareAsync(request.Question, ct);
            var retrieval = await index.SearchAsync(phrasings, scope, ct);

            return Results.Ok(new
            {
                question = request.Question,
                scope = scope.Value,
                phrasings,
                chunks = retrieval.Chunks.Select((c, i) => new
                {
                    rank = i,
                    score = c.Score,
                    source = c.Source,
                    scope = c.Scope,
                    // Enough to eyeball what matched without returning whole
                    // documents to every probe.
                    preview = c.Text.Length > 200 ? c.Text[..200] : c.Text
                })
            });
        }).AddEndpointFilter<AdminKeyFilter>();
    }
}

public record RetrievalProbeRequest(string Question, string? Scope);
