using AiAssistant.Api.Configuration;
using AiAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiAssistant.Api.Services;

// An anonymous visitor gets a small, per-day question budget before they're
// asked to log in through TicketSystem. Rate limiting (RateLimitOptions)
// throttles *speed* for everyone; this throttles *how many questions a
// stranger gets, ever* - a different axis, checked once up front rather than
// per minute.
//
// The check happens before the Ollama call, not after: the whole point of a
// budget is not spending the expensive resource once it's exhausted.
public class GuestLimiter(AiDbContext db, IOptions<AssistantOptions> options)
{
    private readonly int _limit = options.Value.GuestQuestionLimit;

    public async Task<GuestLimitStatus> CheckAsync(string guestKey, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date;

        var askedToday = await db.Questions
            .Where(q => q.GuestKey == guestKey && q.CreatedAt >= since)
            .CountAsync(ct);

        return new GuestLimitStatus(askedToday, _limit);
    }
}

public record GuestLimitStatus(int Asked, int Limit)
{
    public bool IsExceeded => Asked >= Limit;
    public int Remaining => Math.Max(0, Limit - Asked);
}
