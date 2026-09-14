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

        // The list the trace endpoint is entered from: one page of recent
        // questions with just enough per row to decide which one is worth
        // opening. Until now this was only reachable through psql, which meant
        // retrieval quality was inspected by whoever happened to have a
        // database shell open.
        //
        // Admin-only for the same reason the gold set is: this is every user's
        // questions in one response, bulk disclosure by construction.
        app.MapGet("/api/questions", async (
            AiDbContext db,
            int? limit,
            int? offset,
            CancellationToken ct) =>
        {
            // Nullable, unlike gold-set's required `limit`: a minimal API
            // binds a missing non-nullable int to a 400, so `int limit` would
            // make the plain /api/questions call fail instead of defaulting.
            var take = limit is > 0 and <= 200 ? limit.Value : 50;
            var skip = offset is > 0 ? offset.Value : 0;

            var page = await db.Questions.AsNoTracking()
                // ThenBy(Id) makes paging stable: CreatedAt alone leaves two
                // questions written in the same millisecond free to swap
                // places between page 1 and page 2, so one is shown twice and
                // the other never.
                .OrderByDescending(q => q.CreatedAt)
                .ThenByDescending(q => q.Id)
                .Skip(skip)
                .Take(take)
                .Select(q => new
                {
                    q.Id,
                    q.QuestionText,
                    q.Status,
                    q.LanguageDriftSuspected,
                    q.CreatedAt,

                    // AnsweredFromContext is computed during streaming
                    // (ChatOrchestrator: chunks found AND the answer did not
                    // contain the not-found phrase) and never persisted, so it
                    // is reconstructed here from what the row does hold.
                    // ContextUsed is null when retrieval returned nothing, and
                    // a non-"ok" status means the answer never completed - the
                    // decline case is the one this cannot see, so treat this
                    // as "context reached the model", not as a quality verdict.
                    AnsweredFromContext = q.ContextUsed != null && q.Status == "ok",

                    // One correlated subquery per row, emitted as a single
                    // JOIN by EF - not a lookup per question. Deduplicated
                    // after materialisation rather than with Distinct() here,
                    // because a Distinct inside a projected collection is not
                    // reliably translatable and would fall back to throwing at
                    // query time rather than at build time.
                    SourceNames = q.RequestChunks
                        .Select(rc => rc.ContentChunk.ExtractedContent.SourceFileName
                            ?? (rc.ContentChunk.ExtractedContent.TargetPage != null
                                ? rc.ContentChunk.ExtractedContent.TargetPage.Url
                                : null))
                        .ToList()
                })
                .ToListAsync(ct);

            var questions = page.Select(q => new QuestionListItem(
                q.Id,
                q.QuestionText,
                q.Status,
                q.AnsweredFromContext,
                q.LanguageDriftSuspected,
                q.CreatedAt,
                q.SourceNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()));

            return Results.Ok(questions);
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

// A named record rather than the anonymous type the other endpoints project
// straight into, because this one is post-processed after materialisation
// (source deduplication) and an anonymous type cannot be named in that step.
public record QuestionListItem(
    int Id,
    string QuestionText,
    string Status,
    bool AnsweredFromContext,
    bool LanguageDriftSuspected,
    DateTime CreatedAt,
    IReadOnlyList<string> Sources);
