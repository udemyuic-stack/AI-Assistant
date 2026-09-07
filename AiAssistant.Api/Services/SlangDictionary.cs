using AiAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AiAssistant.Api.Services;

// Maps colloquial and cross-script terms onto the wording used in official
// documents. Deterministic and free - unlike model-based expansion - so it
// runs on every question regardless of the host's speed.
//
// Entries live in the database rather than in code because the ones that
// matter are discovered from real questions that returned nothing, and an
// admin adding one shouldn't need a deploy.
public class SlangDictionary(AiDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const string CacheKey = "slang:all";

    public string Normalize(string question)
    {
        var entries = GetEntries();
        if (entries.Count == 0) return question;

        var result = question;
        foreach (var (term, canonical) in entries)
            result = ReplaceWholeWord(result, term, canonical);

        return result;
    }

    private IReadOnlyDictionary<string, string> GetEntries() =>
        cache.GetOrCreate(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return db.SlangTerms.AsNoTracking()
                .ToDictionary(t => t.Term, t => t.CanonicalForm, StringComparer.OrdinalIgnoreCase)
                as IReadOnlyDictionary<string, string>;
        }) ?? new Dictionary<string, string>();

    // Whole-word only: replacing substrings would corrupt longer words that
    // merely contain the term.
    private static string ReplaceWholeWord(string text, string term, string replacement)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (string.Equals(words[i].Trim(), term, StringComparison.OrdinalIgnoreCase))
                words[i] = replacement;

        return string.Join(' ', words);
    }

    public void Invalidate() => cache.Remove(CacheKey);
}
