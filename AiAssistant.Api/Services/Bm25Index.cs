using System.Text.RegularExpressions;
using AiAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AiAssistant.Api.Services;

// The sparse half of hybrid search: BM25 term weights, computed from this
// corpus's own statistics rather than a fixed vocabulary or an external
// model. Complements dense search rather than replacing it - a dense vector
// finds "temir yo'l" for a question about "railway", BM25 finds an exact
// company name, code, or number a paraphrase-tolerant embedding can blur
// past. Found directly on this corpus: a negative-control question scored
// two Toshkent-article chunks in the top 5 for "Buxoro konsessiya" purely on
// topical similarity - term overlap would never have ranked them there.
//
// Corpus statistics (document frequency per term, average chunk length) are
// recomputed from Postgres rather than maintained incrementally in their own
// table. At this corpus's size - a few hundred chunks - a full scan costs
// low-single-digit milliseconds, cheap enough that a dedicated stats table
// with its own migration and its own "does a delete decrement this
// correctly" edge cases is not worth building yet. Revisit past roughly
// 100k chunks, where a full Postgres scan per cache refresh stops being free.
//
// A consequence of recomputing rather than maintaining incrementally: a
// chunk's sparse vector reflects the corpus statistics at the moment it was
// indexed, not the current ones. As the corpus grows, older chunks' stored
// weights drift slightly stale until they are re-ingested. Every real BM25
// system behaves this way - Lucene does not rewrite old postings when new
// documents arrive either - and re-embedding the whole corpus on every
// ingest to keep every vector perfectly current would cost far more than the
// staleness itself ever does.
public partial class Bm25Index(AiDbContext db, IMemoryCache cache)
{
    private const string CacheKey = "bm25-corpus-stats";
    private static readonly TimeSpan StatsTtl = TimeSpan.FromMinutes(5);

    // Standard BM25 defaults (Robertson/Sparck-Jones). Not exposed as a
    // SysParam like the dense-search knobs - these tune term-frequency
    // saturation and length normalisation, not retrieval breadth, and
    // getting them wrong produces confusing rather than merely suboptimal
    // rankings. Revisit if the gold-set ever shows a concrete reason to.
    private const double K1 = 1.5;
    private const double B = 0.75;

    // Splits on anything that is not a Unicode letter or digit, so the same
    // pattern tokenizes Uzbek Latin, Uzbek Cyrillic, and Russian without a
    // per-script branch. Deliberately case-folded and un-stemmed: this is
    // the exact-match complement to a dense vector's fuzzy one, and stemming
    // it would just turn it into a second dense-ish search that duplicates
    // what bge-m3 already does.
    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex TokenPattern();

    public static IReadOnlyList<string> Tokenize(string text) =>
        TokenPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();

    // A stable, table-free mapping from term to Qdrant sparse index. Hashing
    // instead of an assigned-id vocabulary table means the same term always
    // lands on the same dimension without a lookup, and a term seen for the
    // first time needs no write before it can be searched. FNV-1a over
    // xxHash/MurmurHash for no reason beyond being trivial to write
    // correctly inline without a new package; collision risk in a 32-bit
    // space is not a real concern for a vocabulary this corpus will ever
    // reach.
    public static uint TermIndex(string term)
    {
        const uint fnvPrime = 16777619;
        var hash = 2166136261;
        foreach (var ch in term)
        {
            hash ^= ch;
            hash *= fnvPrime;
        }
        return hash;
    }

    public async Task<Bm25Stats> GetStatsAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out Bm25Stats? cached) && cached is not null)
            return cached;

        var texts = await db.ContentChunks.AsNoTracking().Select(c => c.Text).ToListAsync(ct);
        var stats = BuildStats(texts);

        cache.Set(CacheKey, stats, StatsTtl);
        return stats;
    }

    // Called right after a new batch is chunked, before those chunks exist in
    // Postgres - GetStatsAsync alone would compute weights that ignore the
    // very chunks being indexed, including terms that only they introduce.
    // Folding the batch into the cached corpus stats in memory (without
    // writing anything) gives each new chunk a self-consistent document
    // frequency that counts its own batch-mates, the same way it will be
    // counted once every later caller's GetStatsAsync picks it up from
    // Postgres after this ingest commits.
    public async Task<Bm25Stats> GetStatsWithPendingAsync(IReadOnlyList<string> pendingTexts, CancellationToken ct)
    {
        var baseStats = await GetStatsAsync(ct);
        if (pendingTexts.Count == 0) return baseStats;

        var documentFrequency = new Dictionary<string, int>(baseStats.DocumentFrequency);
        var totalChunks = baseStats.TotalChunks;
        var totalTokens = baseStats.TotalTokens;

        foreach (var text in pendingTexts)
        {
            var tokens = Tokenize(text);
            totalChunks++;
            totalTokens += tokens.Count;

            foreach (var term in tokens.Distinct())
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
        }

        return new Bm25Stats(documentFrequency, totalChunks, totalTokens);
    }

    // Document-side weight: full BM25 with term-frequency saturation (k1) and
    // length normalisation (b) against the corpus's average chunk length.
    public static SparseVector ComputeDocumentVector(string text, Bm25Stats stats)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return SparseVector.Empty;

        var termFrequency = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var lengthNorm = stats.AverageChunkLength > 0
            ? 1 - B + B * tokens.Count / stats.AverageChunkLength
            : 1.0;

        var indices = new List<uint>(termFrequency.Count);
        var values = new List<float>(termFrequency.Count);

        foreach (var (term, tf) in termFrequency)
        {
            var weight = Idf(term, stats) * (tf * (K1 + 1)) / (tf + K1 * lengthNorm);
            if (weight <= 0) continue;

            indices.Add(TermIndex(term));
            values.Add((float)weight);
        }

        return new SparseVector(indices, values);
    }

    // Query-side weight: idf alone, no tf saturation or length norm. A
    // question is not a document being ranked against others - "how many"
    // does not become more important for being typed twice - so only the
    // rarity of each term in the corpus decides its weight here.
    public static SparseVector ComputeQueryVector(string text, Bm25Stats stats)
    {
        var terms = Tokenize(text).Distinct().ToList();
        var indices = new List<uint>(terms.Count);
        var values = new List<float>(terms.Count);

        foreach (var term in terms)
        {
            var weight = Idf(term, stats);
            if (weight <= 0) continue;

            indices.Add(TermIndex(term));
            values.Add((float)weight);
        }

        return new SparseVector(indices, values);
    }

    // The +0.5 smoothing keeps idf finite (never divides by zero) and keeps
    // a term appearing in literally every chunk from going negative, which
    // the classic Robertson-Sparck-Jones idf can otherwise do on a small or
    // skewed corpus.
    private static double Idf(string term, Bm25Stats stats)
    {
        var df = stats.DocumentFrequency.GetValueOrDefault(term);
        return Math.Log(1 + (stats.TotalChunks - df + 0.5) / (df + 0.5));
    }

    private static Bm25Stats BuildStats(IReadOnlyList<string> texts)
    {
        var documentFrequency = new Dictionary<string, int>();
        long totalTokens = 0;

        foreach (var text in texts)
        {
            var tokens = Tokenize(text);
            totalTokens += tokens.Count;

            foreach (var term in tokens.Distinct())
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
        }

        return new Bm25Stats(documentFrequency, texts.Count, totalTokens);
    }
}

public record Bm25Stats(IReadOnlyDictionary<string, int> DocumentFrequency, int TotalChunks, long TotalTokens)
{
    public double AverageChunkLength => TotalChunks > 0 ? (double)TotalTokens / TotalChunks : 0;

    public static Bm25Stats Empty { get; } = new(new Dictionary<string, int>(), 0, 0);
}

// A thin, storage-agnostic stand-in for Qdrant.Client.Grpc.SparseVector -
// Bm25Index has no reason to depend on the Qdrant client just to hand back
// two parallel lists, and keeping it free of that dependency is what makes
// the class straightforward to unit test without a running Qdrant instance.
public record SparseVector(IReadOnlyList<uint> Indices, IReadOnlyList<float> Values)
{
    public static SparseVector Empty { get; } = new([], []);
}
