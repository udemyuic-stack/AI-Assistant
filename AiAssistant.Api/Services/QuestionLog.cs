using AiAssistant.Api.Data;
using AiAssistant.Api.Data.Entities;
using AiAssistant.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Services;

// Writes what actually happened for one question: the answer, the exact
// system prompt and context the model saw, and which chunks fed it, ranked.
//
// This is the trace the guide calls out as the thing that most often lies -
// a system that logs the question and answer but not the prompt cannot
// answer "why did it say that" after the fact. Recording the resolved prompt,
// not the template, is the whole point.
//
// Split into Begin/Complete rather than one call after generation finishes:
// on a slow host, the client (browser, curl, a load balancer) can give up
// before the model does. A row written only at the end is a row that never
// gets written for exactly the requests worth investigating. Begin marks
// intent; Complete - called from a finally block regardless of how the
// stream ended - marks outcome.
public class QuestionLog(AiDbContext db, ILogger<QuestionLog> logger)
{
    public async Task<int> BeginAsync(
        string questionText,
        string systemPrompt,
        RetrievalScope? scope,
        int? userId,
        string? guestKey,
        CancellationToken ct = default)
    {
        var question = new Question
        {
            QuestionText = questionText,
            SystemPromptUsed = systemPrompt,
            TicketScope = scope?.Value,
            UserId = userId,
            GuestKey = userId is null ? guestKey : null,
            Status = "pending"
        };

        db.Questions.Add(question);
        await db.SaveChangesAsync(ct);
        return question.Id;
    }

    // ct defaults to None deliberately: this call is meant to run from a
    // finally block after the caller's own token may already be cancelled,
    // and a trace for a cancelled question is the case this exists for.
    public async Task CompleteAsync(
        int questionId,
        AnswerSummary summary,
        string status,
        CancellationToken ct = default)
    {
        var question = await db.Questions.FindAsync([questionId], ct);
        if (question is null)
        {
            logger.LogWarning("CompleteAsync: savol {QuestionId} topilmadi.", questionId);
            return;
        }

        question.AnswerText = summary.FullText.Length > 0 ? summary.FullText : null;
        question.ContextUsed = summary.ContextUsed.Count > 0
            ? string.Join("\n\n---\n\n", summary.ContextUsed.Select(c => c.Text))
            : null;
        question.Status = status;
        question.LanguageDriftSuspected = summary.LanguageDriftSuspected;

        await db.SaveChangesAsync(ct);
        await LinkChunksAsync(questionId, summary.ContextUsed, ct);
    }

    // Chunks are matched back to their audit rows by Qdrant point id. A chunk
    // whose row is missing (indexed before the audit trail existed, or
    // re-indexed since) is skipped rather than failing the write - losing one
    // trace link is better than losing the whole question record.
    private async Task LinkChunksAsync(
        int questionId,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct)
    {
        if (chunks.Count == 0) return;

        var pointIds = chunks.Select(c => c.QdrantPointId).ToList();
        var knownChunks = await db.ContentChunks
            .Where(c => pointIds.Contains(c.QdrantPointId))
            .Select(c => new { c.Id, c.QdrantPointId })
            .ToDictionaryAsync(c => c.QdrantPointId, c => c.Id, ct);

        var links = chunks
            .Select((chunk, rank) => (chunk, rank))
            .Where(entry => knownChunks.ContainsKey(entry.chunk.QdrantPointId))
            .Select(entry => new RequestChunk
            {
                QuestionId = questionId,
                ContentChunkId = knownChunks[entry.chunk.QdrantPointId],
                Score = entry.chunk.Score,
                Rank = entry.rank
            })
            .ToList();

        if (links.Count < chunks.Count)
            logger.LogWarning(
                "Trace to'liq emas: {Linked}/{Total} bo'lak bog'landi. QuestionId={QuestionId}",
                links.Count, chunks.Count, questionId);

        if (links.Count == 0) return;

        db.RequestChunks.AddRange(links);
        await db.SaveChangesAsync(ct);
    }
}
