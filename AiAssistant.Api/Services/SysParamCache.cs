using AiAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AiAssistant.Api.Services;

// Runtime-tunable settings, read on every question but changed rarely (an
// admin moving a slider). A 60s TTL cache means a setting change reaches
// traffic within a minute without costing a database round-trip per
// question - the shape the book's own get_sys_param() uses, for the same
// reason: the read is hot, the write is not.
public class SysParamCache(AiDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<T> GetAsync<T>(string key, T fallback, CancellationToken ct = default)
    {
        var raw = await cache.GetOrCreateAsync(CacheKey(key), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return await db.SysParams.AsNoTracking()
                .Where(p => p.Key == key)
                .Select(p => p.Value)
                .FirstOrDefaultAsync(ct);
        });

        if (raw is null) return fallback;

        try
        {
            return (T)Convert.ChangeType(raw, typeof(T));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            // A bad value in the table must not take the RAG pipeline down -
            // fall back and let whoever set it notice the value never applies.
            return fallback;
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var existing = await db.SysParams.FindAsync([key], ct);
        if (existing is null)
            db.SysParams.Add(new Data.Entities.SysParam { Key = key, Value = value });
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey(key));
    }

    private static string CacheKey(string key) => $"sysparam:{key}";
}
