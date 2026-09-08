using AiAssistant.Api.Data;
using AiAssistant.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Endpoints;

// Pre-written questions a guest can tap instead of typing - keeps an
// anonymous visitor's limited question budget (GuestLimiter) from being
// spent on questions the index has no chance of answering.
public static class RubricEndpoints
{
    public static void MapRubricEndpoints(this WebApplication app)
    {
        app.MapGet("/api/rubrics", async (AiDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Rubrics.AsNoTracking()
                .Where(r => r.IsActive)
                .OrderBy(r => r.SortOrder)
                .ToListAsync(ct)));

        app.MapPost("/api/rubrics", async (CreateRubricRequest request, AiDbContext db, CancellationToken ct) =>
        {
            var rubric = new Rubric { QuestionText = request.QuestionText, SortOrder = request.SortOrder };
            db.Rubrics.Add(rubric);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/rubrics/{rubric.Id}", rubric);
        });
    }
}

public record CreateRubricRequest(string QuestionText, int SortOrder = 0);
