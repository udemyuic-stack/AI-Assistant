using AiAssistant.Api.Data;
using AiAssistant.Api.Data.Entities;
using AiAssistant.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Endpoints;

public static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        // Rates one answer green or red. Re-rating updates the existing row
        // rather than adding a second - one verdict per question.
        //
        // The caller passes userId, which this service trusts. That is only
        // safe because the API is not internet-facing: TicketSystem
        // authenticates the user and calls this server-side. If that ever
        // changes, ownership has to be re-established here from a token,
        // not taken from the request body.
        app.MapPost("/api/feedback", async (
            FeedbackRequest request,
            AiDbContext db,
            CancellationToken ct) =>
        {
            if (request.Code is not ("green" or "red"))
                return Results.BadRequest(new { error = "Code 'green' yoki 'red' bo'lishi kerak." });

            var question = await db.Questions
                .Include(q => q.Feedback)
                .FirstOrDefaultAsync(q => q.Id == request.QuestionId, ct);

            if (question is null)
                return Results.NotFound(new { error = $"Savol {request.QuestionId} topilmadi." });

            // A question asked by one user must not be rated by another.
            if (question.UserId is not null && question.UserId != request.UserId)
                return Results.Forbid();

            if (question.Feedback is null)
            {
                db.FeedbackMarks.Add(new FeedbackMark
                {
                    QuestionId = request.QuestionId,
                    UserId = request.UserId,
                    Code = request.Code,
                    Comment = request.Comment
                });
            }
            else
            {
                question.Feedback.Code = request.Code;
                question.Feedback.Comment = request.Comment;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "saved" });
        });

        // The gold set: questions a human confirmed were answered correctly.
        // Re-run these after any retrieval or prompt change and compare the
        // pass rate - without this, every later tuning decision (rerank,
        // expansion, chunk size) is guesswork.
        //
        // Admin-only: this returns every user's questions and answers in one
        // response, which is exactly the kind of bulk disclosure that must not
        // be reachable without a key.
        app.MapGet("/api/evaluation/gold-set", async (
            AiDbContext db,
            int limit,
            CancellationToken ct) =>
        {
            var goldSet = await db.Questions.AsNoTracking()
                .Where(q => q.Feedback != null && q.Feedback.Code == "green" && q.AnswerText != null)
                .OrderByDescending(q => q.CreatedAt)
                .Take(limit is > 0 and <= 200 ? limit : 50)
                .Select(q => new
                {
                    q.Id,
                    q.QuestionText,
                    q.AnswerText,
                    q.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(goldSet);
        }).AddEndpointFilter<AdminKeyFilter>();

        // Full trace for one answer: the prompt the model saw and the chunks
        // that fed it, ranked. This is what makes "why did it say that"
        // answerable after the fact.
        //
        // Admin-only: a question id is a small integer, so without a gate
        // anyone could walk 1..N and read every user's questions, answers,
        // and the source material behind them.
        app.MapGet("/api/questions/{questionId:int}/trace", async (
            int questionId,
            AiDbContext db,
            CancellationToken ct) =>
        {
            var trace = await db.Questions.AsNoTracking()
                .Where(q => q.Id == questionId)
                .Select(q => new
                {
                    q.Id,
                    q.QuestionText,
                    q.AnswerText,
                    q.SystemPromptUsed,
                    q.ContextUsed,
                    q.Status,
                    q.LanguageDriftSuspected,
                    q.CreatedAt,
                    Chunks = q.RequestChunks
                        .OrderBy(rc => rc.Rank)
                        .Select(rc => new
                        {
                            rc.Rank,
                            rc.Score,
                            rc.ContentChunk.Text,
                            rc.ContentChunk.Scope
                        })
                })
                .FirstOrDefaultAsync(ct);

            return trace is null
                ? Results.NotFound(new { error = $"Savol {questionId} topilmadi." })
                : Results.Ok(trace);
        }).AddEndpointFilter<AdminKeyFilter>();
    }
}

public record FeedbackRequest(int QuestionId, string Code, string UserId, string? Comment);
