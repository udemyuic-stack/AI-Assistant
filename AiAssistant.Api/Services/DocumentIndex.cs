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
    // Named vectors, not the single unnamed one this collection started
    // with: a point needs a name to hang a second vector (sparse) off of,
    // and Qdrant does not allow mixing an unnamed vector with named ones in
    // the same collection. Existing points from before hybrid search are
    // incompatible with this schema - the collection has to be recreated and
    // the corpus re-ingested, the same workflow already used earlier this
    // session for the embedding-model and chunking changes.
    private const string DenseVectorName = "dense";
    private const string SparseVectorName = "sparse";

    private readonly OllamaClient _ollama;
    private readonly DocumentTextExtractor _extractor;
    private readonly Bm25Index _bm25;
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
        Bm25Index bm25,
        QdrantClient qdrant,
        SysParamCache sysParams,
        IOptions<QdrantOptions> qdrantOptions,
        IOptions<RagOptions> ragOptions)
    {
        _ollama = ollama;
        _extractor = extractor;
        _bm25 = bm25;
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

        // Computed against the corpus's stats plus this very batch (see
        // Bm25Index.GetStatsWithPendingAsync) - these chunks are not in
        // Postgres yet, so a plain GetStatsAsync would weight them as if
        // their own terms did not exist.
        var bm25Stats = await _bm25.GetStatsWithPendingAsync(chunks, ct);
        var sparseVectors = chunks.Select(text => Bm25Index.ComputeDocumentVector(text, bm25Stats)).ToList();

        // Reserve a contiguous block atomically, so concurrent ingests never
        // hand out the same id regardless of how many chunks either has or
        // how close together the calls land.
        var baseId = (ulong)Interlocked.Add(ref _nextPointId, chunks.Count) - (ulong)chunks.Count;

        progress?.Report("Bazaga yozilyapti...");
        var points = BuildPoints(baseId, document, language, chunks, vectors, sparseVectors);
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

        var bm25Stats = await _bm25.GetStatsAsync(ct);
        var bestByPoint = new Dictionary<ulong, (RetrievedChunk Chunk, float Score)>();

        foreach (var question in questions)
        {
            var candidates = await HybridSearchAsync(question, bm25Stats, scopeFilter, topK, threshold, ct);

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
        var selected = SelectContext(found, questions[0]);
        var withNeighbours = await ExpandWithNeighboursAsync(selected, ct);
        return new RetrievalResult(withNeighbours);
    }

    // Pulls in the chunk(s) immediately before and after each kept chunk, from
    // the same source document, so a fact split across a chunk boundary
    // reaches the model whole even when only one half of it scored high
    // enough to be selected on its own. One Qdrant scroll per distinct source
    // document among the kept chunks - an exact (objectKey, chunkIndex) match,
    // not a vector search, so it costs a filter lookup, not another embedding
    // or a slower round trip.
    private async Task<IReadOnlyList<RetrievedChunk>> ExpandWithNeighboursAsync(
        IReadOnlyList<RetrievedChunk> chunks, CancellationToken ct)
    {
        var windowSize = _ragOptions.RetrievalWindowSize;
        if (windowSize <= 0) return chunks;

        // ChunkIndex is -1 for anything indexed before this field existed
        // (an older point re-surfacing after a partial re-ingest) - no
        // position to expand from, so it is left exactly as retrieved.
        var eligible = chunks.Where(c => c.ChunkIndex >= 0 && c.ObjectKey.Length > 0).ToList();
        if (eligible.Count == 0) return chunks;

        var present = eligible.Select(c => (c.ObjectKey, c.ChunkIndex)).ToHashSet();
        var neighbours = new List<RetrievedChunk>();

        foreach (var group in eligible.GroupBy(c => c.ObjectKey))
        {
            var wanted = group
                .SelectMany(c => Enumerable.Range(
                    Math.Max(0, c.ChunkIndex - windowSize), windowSize * 2 + 1))
                .Distinct()
                .Where(index => !present.Contains((group.Key, index)))
                .ToList();

            if (wanted.Count == 0) continue;

            var filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "objectKey",
                            Match = new Match { Keyword = group.Key }
                        }
                    },
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "chunkIndex",
                            Match = new Match
                            {
                                Integers = new RepeatedIntegers { Integers = { wanted.Select(i => (long)i) } }
                            }
                        }
                    }
                }
            };

            var response = await _qdrant.ScrollAsync(
                _qdrantOptions.Collection,
                filter: filter,
                limit: (uint)wanted.Count,
                cancellationToken: ct);

            neighbours.AddRange(response.Result.Select(ToChunkFromRetrieved));
        }

        // Capped, not appended wholesale: measured directly on this corpus's
        // gold-set, a question whose selected chunks clustered densely in one
        // long article (Toshkent, 170 chunks) pulled in enough overlapping
        // windows to nearly triple the prompt (8 selected chunks becoming 23
        // total) for no proportional gain - most of that growth was window
        // padding around chunks that were already adjacent to each other.
        // Kept within a budget relative to what SelectContext actually chose,
        // prioritising whichever neighbour sits closest to a selected chunk
        // in its own document when there is more window material than budget.
        var anchorsByDocument = eligible
            .GroupBy(c => c.ObjectKey)
            .ToDictionary(g => g.Key, g => g.Select(c => c.ChunkIndex).ToList());

        var budget = Math.Max(0, _ragOptions.ContextChunks * 2 - chunks.Count);
        var kept = neighbours
            .OrderBy(n => anchorsByDocument.TryGetValue(n.ObjectKey, out var anchors)
                ? anchors.Min(a => Math.Abs(a - n.ChunkIndex))
                : int.MaxValue)
            .Take(budget);

        // Ordered by position within each document, not by score: these
        // pieces exist to be read as one passage, and a reader (human or
        // model) makes sense of "the number, then what it counts" only in
        // the order they were actually written.
        return chunks.Concat(kept)
            .OrderBy(c => c.ObjectKey, StringComparer.Ordinal)
            .ThenBy(c => c.ChunkIndex)
            .ToList();
    }

    // Dense and sparse candidates are fetched separately (Prefetch) and
    // combined by Reciprocal Rank Fusion, not by summing their scores - a
    // cosine similarity and a BM25 weight are not the same unit, so adding
    // them would let whichever happens to have the larger numeric range
    // dominate regardless of actual relevance. RRF only looks at each list's
    // *rank*, which is comparable across arbitrarily different scoring
    // scales by construction.
    //
    // The similarity threshold started out applied to the dense prefetch
    // only, on the reasoning that a BM25 weight has no meaningful
    // relationship to a 0.0-1.0 cosine cutoff. That reasoning was correct but
    // incomplete: it explains why the threshold cannot apply to the sparse
    // side, not what happens when dense finds nothing. RRF fuses by rank, not
    // by score, so a point that only the sparse prefetch surfaced still gets
    // a nonzero fused score even when the dense prefetch returned zero
    // candidates - there is no "everything below the bar" signal for RRF to
    // inherit from a prefetch that came back empty.
    //
    // Measured directly on this corpus's gold-set negative controls before
    // this gate existed: "Buxoro viloyatida nechta konsessiya berilgan?" (no
    // such content anywhere in the corpus) went from correctly returning zero
    // chunks to returning five, on term overlap alone, once BM25 was fused
    // in unconditionally. Every one of the six negative controls broke the
    // same way - the dense threshold's entire "there is nothing here" signal
    // was being overridden by a search method with no floor of its own.
    //
    // The fix is a dense floor, checked as its own query before fusion is
    // attempted at all: if nothing clears the dense threshold, there is
    // nothing to answer from, and BM25 does not get a vote on that - it
    // exists to catch an exact term dense underweights among results dense
    // already considers relevant, not to resurrect a topic dense rejected
    // outright. The extra round trip this costs is a Qdrant query
    // (single-digit milliseconds), negligible next to the embedding call
    // beside it.
    private async Task<IReadOnlyList<ScoredPoint>> HybridSearchAsync(
        string question,
        Bm25Stats bm25Stats,
        Filter scopeFilter,
        int topK,
        float threshold,
        CancellationToken ct)
    {
        var denseVector = await _ollama.EmbedAsync(question, ct);
        var denseInput = new VectorInput { Dense = ToDenseVector(denseVector) };

        var denseOnly = await _qdrant.QueryAsync(
            _qdrantOptions.Collection,
            query: new Query { Nearest = denseInput },
            usingVector: DenseVectorName,
            filter: scopeFilter,
            scoreThreshold: threshold,
            limit: (ulong)topK,
            cancellationToken: ct);

        if (denseOnly.Count == 0) return denseOnly;

        var sparseVector = Bm25Index.ComputeQueryVector(question, bm25Stats);
        if (sparseVector.Indices.Count == 0) return denseOnly;

        var densePrefetch = new PrefetchQuery
        {
            Query = new Query { Nearest = denseInput },
            Using = DenseVectorName,
            Filter = scopeFilter,
            ScoreThreshold = threshold,
            Limit = (ulong)topK
        };

        // sparseVector.Indices is already known non-empty (checked above,
        // before the dense floor could return early), so both prefetches are
        // always present here.
        var prefetch = new List<PrefetchQuery>
        {
            densePrefetch,
            new()
            {
                Query = new Query { Nearest = new VectorInput { Sparse = ToSparseVector(sparseVector) } },
                Using = SparseVectorName,
                Filter = scopeFilter,
                Limit = (ulong)topK
            }
        };

        // payloadSelector left at its default, matching the old SearchAsync
        // call this replaced - that call never set it either and ToChunk has
        // always read the payload back successfully, so the default already
        // includes it.
        return await _qdrant.QueryAsync(
            _qdrantOptions.Collection,
            query: new Query { Fusion = Fusion.Rrf },
            prefetch: prefetch,
            limit: (ulong)topK,
            cancellationToken: ct);
    }

    private static DenseVector ToDenseVector(float[] values)
    {
        var vector = new DenseVector();
        vector.Data.Add(values);
        return vector;
    }

    private static Qdrant.Client.Grpc.SparseVector ToSparseVector(SparseVector sparse)
    {
        var vector = new Qdrant.Client.Grpc.SparseVector();
        vector.Indices.Add(sparse.Indices);
        vector.Values.Add(sparse.Values);
        return vector;
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
        IReadOnlyList<float[]> vectors,
        IReadOnlyList<SparseVector> sparseVectors)
    {
        var points = new List<PointStruct>(chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            var namedVectors = new NamedVectors();
            namedVectors.Vectors.Add(DenseVectorName, vectors[i]);

            // Omitted entirely rather than stored empty: a chunk that shares
            // no vocabulary with anything (rare, but a lone code fragment or
            // a chunk that is almost all numbers can do this) would otherwise
            // write a sparse vector with zero entries, which Qdrant accepts
            // but which can never be a Prefetch match for any query - there
            // is no reason to spend the write.
            if (sparseVectors[i].Indices.Count > 0)
                namedVectors.Vectors.Add(SparseVectorName, new Vector { Sparse = ToSparseVector(sparseVectors[i]) });

            var point = new PointStruct
            {
                Id = new PointId { Num = baseId + (ulong)i },
                Vectors = new Vectors { Vectors_ = namedVectors }
            };
            point.Payload.Add("text", new Value { StringValue = chunks[i] });
            point.Payload.Add("source", new Value { StringValue = document.FileName });
            point.Payload.Add("scope", new Value { StringValue = document.Scope.Value });
            point.Payload.Add("objectKey", new Value { StringValue = document.ObjectKey });
            // This chunk's position within its own document - the key that
            // lets a later search find "the chunk right after this one" via
            // an exact payload filter (objectKey + chunkIndex), rather than
            // guessing from QdrantPointId adjacency, which only holds within
            // a single ingest's own reserved id block and silently breaks
            // across a batch boundary shared with an unrelated document.
            point.Payload.Add("chunkIndex", new Value { IntegerValue = i });
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
        Scope: Payload(point, "scope"),
        ObjectKey: Payload(point, "objectKey"),
        ChunkIndex: (int)IntPayload(point, "chunkIndex"));

    private static string Payload(ScoredPoint point, string key) =>
        point.Payload.TryGetValue(key, out var value) ? value.StringValue : string.Empty;

    // -1 for a point indexed before chunkIndex existed: ExpandWithNeighboursAsync
    // treats that as "position unknown" and skips it rather than guessing a
    // neighbour from a made-up index.
    private static long IntPayload(ScoredPoint point, string key) =>
        point.Payload.TryGetValue(key, out var value) ? value.IntegerValue : -1;

    // A neighbour fetched by exact position, not by ranking against the
    // question, has no similarity score of its own - 0 marks that plainly
    // rather than reusing the score of whichever kept chunk it was pulled in
    // next to, which would misrepresent it as independently relevant.
    private static RetrievedChunk ToChunkFromRetrieved(RetrievedPoint point) => new(
        QdrantPointId: point.Id.Num,
        Text: point.Payload.TryGetValue("text", out var text) ? text.StringValue : string.Empty,
        Source: point.Payload.TryGetValue("source", out var source) ? source.StringValue : string.Empty,
        Score: 0f,
        Scope: point.Payload.TryGetValue("scope", out var scope) ? scope.StringValue : string.Empty,
        ObjectKey: point.Payload.TryGetValue("objectKey", out var key) ? key.StringValue : string.Empty,
        ChunkIndex: point.Payload.TryGetValue("chunkIndex", out var index) ? (int)index.IntegerValue : -1);

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
                var denseVectors = new VectorParamsMap();
                denseVectors.Map.Add(DenseVectorName, new VectorParams
                {
                    Size = _qdrantOptions.VectorSize,
                    Distance = Distance.Cosine
                });

                var sparseVectors = new SparseVectorConfig();
                sparseVectors.Map.Add(SparseVectorName, new SparseVectorParams());

                await _qdrant.CreateCollectionAsync(
                    _qdrantOptions.Collection,
                    denseVectors,
                    sparseVectorsConfig: sparseVectors,
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
