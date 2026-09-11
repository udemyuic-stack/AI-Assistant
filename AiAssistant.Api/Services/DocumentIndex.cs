using AiAssistant.Api.Configuration;
using AiAssistant.Api.Models;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AiAssistant.Api.Services;

// Stores document chunks and retrieves the ones relevant to a question.
// Named for its role rather than its store: the vector backend already changed
// once (pgvector to Qdrant) and the callers never needed to know.
//
// Every point carries a scope payload. A search reads the global knowledge base
// plus, optionally, one conversation's own uploads - never another's.
//
// Language is stored as metadata but deliberately never filtered on. Filtering
// by exact language match is what makes a correct question return nothing when
// the source documents happen to be tagged in another language.
public class DocumentIndex
{
    private readonly OllamaClient _ollama;
    private readonly DocumentTextExtractor _extractor;
    private readonly QdrantClient _qdrant;
    private readonly QdrantOptions _qdrantOptions;
    private readonly RagOptions _ragOptions;
    private readonly SysParamCache _sysParams;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _collectionReady;

    // Process-wide, not per-call: two ingests landing in the same millisecond
    // used to collide on point IDs (baseId + i), silently overwriting one
    // upload's vectors with another's. Seeded from the clock so IDs stay
    // roughly time-ordered - useful for debugging in Qdrant's own tools -
    // but uniqueness comes from the atomic increment, not the timestamp.
    private static long _nextPointId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    // QdrantClient is injected, not constructed here, so its lifetime (a
    // Singleton connection) stays independent of this class's own lifetime.
    // DocumentIndex needs SysParamCache, which needs AiDbContext, which is
    // Scoped - making DocumentIndex Singleton too would make it a captive
    // dependency holding a Scoped service past its intended lifetime.
    public DocumentIndex(
        OllamaClient ollama,
        DocumentTextExtractor extractor,
        QdrantClient qdrant,
        SysParamCache sysParams,
        IOptions<QdrantOptions> qdrantOptions,
        IOptions<RagOptions> ragOptions)
    {
        _ollama = ollama;
        _extractor = extractor;
        _qdrant = qdrant;
        _sysParams = sysParams;
        _qdrantOptions = qdrantOptions.Value;
        _ragOptions = ragOptions.Value;
    }

    // filePath points at a local copy of the document; DocumentLibrary owns
    // that file's lifetime. Returns the chunks that reached the index, so a
    // caller that needs an audit trail (ContentChunk rows) doesn't have to
    // recompute point IDs or re-split the text to get it.
    public async Task<IReadOnlyList<IndexedChunk>> IndexFileAsync(
        string filePath,
        StoredDocument document,
        string? language = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureCollectionAsync(ct);

        progress?.Report("Matn ajratilyapti...");
        var chunks = _extractor.ExtractChunks(filePath, document.FileName);
        if (chunks.Count == 0)
        {
            progress?.Report("Hujjatdan matn topilmadi.");
            return [];
        }

        var vectors = await EmbedInBatchesAsync(chunks, progress, ct);

        // Reserve a contiguous block atomically, so concurrent ingests never
        // hand out the same id regardless of how many chunks either has or
        // how close together the calls land.
        var baseId = (ulong)Interlocked.Add(ref _nextPointId, chunks.Count) - (ulong)chunks.Count;

        progress?.Report("Bazaga yozilyapti...");
        var points = BuildPoints(baseId, document, language, chunks, vectors);
        await _qdrant.UpsertAsync(_qdrantOptions.Collection, points, cancellationToken: ct);

        progress?.Report($"Tayyor: {chunks.Count} ta bo'lak saqlandi.");
        return chunks.Select((text, i) => new IndexedChunk(baseId + (ulong)i, text)).ToList();
    }

    // scope: pass a conversation's scope to search its own uploads alongside
    // the shared knowledge base. Omit it for global-only retrieval.
    public Task<RetrievalResult> SearchAsync(
        string question,
        RetrievalScope? scope = null,
        CancellationToken ct = default) =>
        SearchAsync([question], scope, ct);

    // Multi-query retrieval: each phrasing is searched separately and the
    // results are merged. A document only reachable by its Russian wording
    // still surfaces for an Uzbek question, which single-query vector search
    // cannot do (QueryPreprocessor produces the phrasings).
    //
    // Merge keeps the best score per point: the same chunk found by two
    // phrasings is one result, ranked by whichever phrasing matched it best.
    public async Task<RetrievalResult> SearchAsync(
        IReadOnlyList<string> questions,
        RetrievalScope? scope = null,
        CancellationToken ct = default)
    {
        await EnsureCollectionAsync(ct);

        // Admin-tunable, DB-backed with the appsettings value as fallback -
        // a slider change reaches traffic within the cache's TTL, no redeploy.
        var topK = await _sysParams.GetAsync(SysParamKeys.RagTopK, _ragOptions.TopK, ct);
        var threshold = await _sysParams.GetAsync(
            SysParamKeys.RagSimilarityThreshold, _ragOptions.SimilarityThreshold, ct);

        var readable = scope is null || scope.Value.IsGlobal
            ? [RetrievalScope.GlobalValue]
            : new[] { RetrievalScope.GlobalValue, scope.Value.Value };
        var scopeFilter = MatchAnyScope(readable);

        var bestByPoint = new Dictionary<ulong, (RetrievedChunk Chunk, float Score)>();

        foreach (var question in questions)
        {
            var vector = await _ollama.EmbedAsync(question, ct);

            var candidates = await _qdrant.SearchAsync(
                collectionName: _qdrantOptions.Collection,
                vector: vector,
                filter: scopeFilter,
                limit: (ulong)topK,
                scoreThreshold: threshold,
                cancellationToken: ct);

            foreach (var candidate in candidates)
            {
                var pointId = candidate.Id.Num;
                if (bestByPoint.TryGetValue(pointId, out var existing) && existing.Score >= candidate.Score)
                    continue;

                bestByPoint[pointId] = (ToChunk(candidate), candidate.Score);
            }
        }

        if (bestByPoint.Count == 0) return RetrievalResult.Empty;

        var found = bestByPoint.Values
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.Chunk)
            .ToList();

        // The first phrasing is the user's own words - the right one to
        // stem-match against, since the expansions are the model's paraphrase.
        return new RetrievalResult(SelectContext(found, questions[0]));
    }

    // Vector search alone misses inflected forms: Uzbek and Russian both attach
    // suffixes to a shared root, so "litsenziya" and "litsenziyasini" can land
    // far apart. The top matches are kept as-is, then any lower-ranked chunk
    // sharing a six-character prefix with a query word is pulled back in.
    private IReadOnlyList<RetrievedChunk> SelectContext(
        IReadOnlyList<RetrievedChunk> candidates,
        string question)
    {
        var keep = _ragOptions.ContextChunks;
        var primary = candidates.Take(keep / 2 + 1).ToList();

        var stems = question
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 3)
            .Select(word => word[..Math.Min(6, word.Length)].ToLowerInvariant())
            .Distinct()
            .ToList();

        var keywordMatches = candidates
            .Skip(primary.Count)
            .Where(chunk => stems.Any(stem => chunk.Text.ToLowerInvariant().Contains(stem)));

        return primary.Concat(keywordMatches).Take(keep).ToList();
    }

    private async Task<List<float[]>> EmbedInBatchesAsync(
        IReadOnlyList<string> chunks,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        const int batchSize = 32;
        var totalBatches = (int)Math.Ceiling(chunks.Count / (double)batchSize);
        var vectors = new List<float[]>(chunks.Count);

        for (var start = 0; start < chunks.Count; start += batchSize)
        {
            var batch = chunks.Skip(start).Take(batchSize).ToList();
            progress?.Report(
                $"{start / batchSize + 1}/{totalBatches} to'plam vektorlanyapti ({chunks.Count} bo'lak)...");
            vectors.AddRange(await _ollama.EmbedBatchAsync(batch, ct));
        }

        return vectors;
    }

    private static List<PointStruct> BuildPoints(
        ulong baseId,
        StoredDocument document,
        string? language,
        IReadOnlyList<string> chunks,
        IReadOnlyList<float[]> vectors)
    {
        var points = new List<PointStruct>(chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            var vector = new Vector();
            vector.Data.AddRange(vectors[i]);

            var point = new PointStruct
            {
                Id = new PointId { Num = baseId + (ulong)i },
                Vectors = new Vectors { Vector = vector }
            };
            point.Payload.Add("text", new Value { StringValue = chunks[i] });
            point.Payload.Add("source", new Value { StringValue = document.FileName });
            point.Payload.Add("scope", new Value { StringValue = document.Scope.Value });
            point.Payload.Add("objectKey", new Value { StringValue = document.ObjectKey });
            if (!string.IsNullOrEmpty(language))
                point.Payload.Add("language", new Value { StringValue = language });

            points.Add(point);
        }

        return points;
    }

    private static Filter MatchAnyScope(IEnumerable<string> scopes) => new()
    {
        Should =
        {
            scopes.Select(scope => new Condition
            {
                Field = new FieldCondition
                {
                    Key = "scope",
                    Match = new Match { Keyword = scope }
                }
            })
        }
    };

    private static RetrievedChunk ToChunk(ScoredPoint point) => new(
        QdrantPointId: point.Id.Num,
        Text: Payload(point, "text"),
        Source: Payload(point, "source"),
        Score: point.Score,
        Scope: Payload(point, "scope"));

    private static string Payload(ScoredPoint point, string key) =>
        point.Payload.TryGetValue(key, out var value) ? value.StringValue : string.Empty;

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        if (_collectionReady) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_collectionReady) return;

            var collections = await _qdrant.ListCollectionsAsync(ct);
            if (!collections.Contains(_qdrantOptions.Collection))
            {
                await _qdrant.CreateCollectionAsync(
                    _qdrantOptions.Collection,
                    new VectorParams
                    {
                        Size = _qdrantOptions.VectorSize,
                        Distance = Distance.Cosine
                    },
                    cancellationToken: ct);
            }

            _collectionReady = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
